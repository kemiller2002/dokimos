namespace Dokimos.Core

open System
open System.Text.RegularExpressions
open System.Xml.Linq

/// One entry of the candidate commit's tree, read from Git objects.
type TreeEntry = { Mode: string; Blob: string }

/// The candidate as Git recorded it at one commit. Contents hold only the
/// blobs Dokimos needed to read; nothing here comes from a working tree.
type CandidateSnapshot =
    { Commit: string
      CommittedAt: DateTimeOffset
      Message: string
      Entries: Map<string, TreeEntry>
      Contents: Map<string, byte[]>
      /// Paths whose committed bytes carry the Ordo generated-file marker.
      MarkerPaths: string list
      /// Project and package files at the candidate commit.
      ProjectFiles: Map<string, string> }

/// The comparison point for dependency and protected-path diffs.
type BaseSnapshot =
    { Commit: string
      Entries: Map<string, TreeEntry>
      ProjectFiles: Map<string, string> }

type HostObservation =
    { Id: string
      AttemptId: string
      Kind: string
      Path: string
      /// denied | allowed | unknown, as the recorder saw it.
      Outcome: string
      RecordedBy: string
      Protected: bool
      Detail: string }

type PlatformProof = { Name: string; Status: string; Detail: string }

/// Host-recorded mutation and containment evidence (`dokimos.host-observations/1`).
type HostEvidence =
    { HostId: string
      Recorder: string
      Enforcement: string
      Mechanism: string
      PlatformProofs: PlatformProof list
      Observations: HostObservation list }

type DenialClaim = { AttemptId: string; Path: string; Evidence: string }

type TestClaim = { Id: string; Outcome: string; Source: string }

/// What a constructor, host or worker says happened. Claims are verified,
/// never counted as evidence (`dokimos.construction-claims/1`).
type ConstructionClaims =
    { Source: string
      Confinement: string
      Denials: DenialClaim list
      Tests: TestClaim list }

type DependencyAuthorization = { Project: string; Kind: string; Include: string; Change: string }

type Waiver = { RequirementKey: string; Reason: string; Owner: string }

type FindingException =
    { Code: string
      Subject: string
      Reason: string
      Owner: string
      Expires: DateTimeOffset option }

/// Authorization and exception evidence (`dokimos.construction-authorization/1`).
type ConstructionAuthorization =
    { Authority: string
      Dependencies: DependencyAuthorization list
      Waivers: Waiver list
      Exceptions: FindingException list }

/// One package or project reference in a project file.
type DependencyReference = { Project: string; Kind: string; Include: string; Version: string }

type DependencyChange = { Reference: DependencyReference; Change: string }

[<RequireQualifiedAccess>]
module ConstructionEvidence =
    let private s = ConstructionJson.text

    let emptyAuthorization = { Authority = ""; Dependencies = []; Waivers = []; Exceptions = [] }

    let readHost (json: string) : Result<HostEvidence, string> =
        ConstructionJson.parse "host observations" json
        |> Result.bind (fun root ->
            if s "schema" root <> "dokimos.host-observations/1" then Error("host observations: unsupported schema " + s "schema" root)
            else
                let host = ConstructionJson.get "host" root |> Option.defaultValue root
                Ok
                    { HostId = s "id" host
                      Recorder = s "recorder" host
                      Enforcement = s "enforcement" host
                      Mechanism = s "mechanism" host
                      PlatformProofs =
                        ConstructionJson.array "platformProofs" host
                        |> List.map (fun p -> { Name = s "name" p; Status = s "status" p; Detail = s "detail" p })
                      Observations =
                        ConstructionJson.array "observations" root
                        |> List.map (fun o ->
                            { Id = s "id" o; AttemptId = s "attemptId" o; Kind = s "kind" o; Path = s "path" o
                              Outcome = s "outcome" o; RecordedBy = s "recordedBy" o; Detail = s "detail" o
                              Protected =
                                ConstructionJson.get "protected" o
                                |> Option.exists (fun v -> v.ValueKind = Text.Json.JsonValueKind.True) }) })

    let readClaims (json: string) : Result<ConstructionClaims, string> =
        ConstructionJson.parse "claims" json
        |> Result.bind (fun root ->
            if s "schema" root <> "dokimos.construction-claims/1" then Error("claims: unsupported schema " + s "schema" root)
            else
                Ok
                    { Source = s "source" root
                      Confinement = s "confinement" root
                      Denials =
                        ConstructionJson.array "denials" root
                        |> List.map (fun d -> { AttemptId = s "attemptId" d; Path = s "path" d; Evidence = s "evidence" d })
                      Tests =
                        ConstructionJson.array "testResults" root
                        |> List.map (fun t -> { Id = s "id" t; Outcome = (s "outcome" t).ToLowerInvariant(); Source = "claims" }) })

    let readAuthorization (json: string) : Result<ConstructionAuthorization, string> =
        ConstructionJson.parse "authorization" json
        |> Result.bind (fun root ->
            if s "schema" root <> "dokimos.construction-authorization/1" then Error("authorization: unsupported schema " + s "schema" root)
            else
                let expires (e: Text.Json.JsonElement) =
                    match DateTimeOffset.TryParse(s "expires" e, Globalization.CultureInfo.InvariantCulture, Globalization.DateTimeStyles.AssumeUniversal) with
                    | true, value -> Some value
                    | _ -> None
                Ok
                    { Authority = s "authority" root
                      Dependencies =
                        ConstructionJson.array "dependencies" root
                        |> List.map (fun d -> { Project = s "project" d; Kind = s "kind" d; Include = s "include" d; Change = s "change" d })
                      Waivers =
                        ConstructionJson.array "waivers" root
                        |> List.map (fun w -> { RequirementKey = s "requirementKey" w; Reason = s "reason" w; Owner = s "owner" w })
                      Exceptions =
                        ConstructionJson.array "exceptions" root
                        |> List.map (fun e -> { Code = s "code" e; Subject = s "subject" e; Reason = s "reason" e; Owner = s "owner" e; Expires = expires e }) })

    /// Per-test outcomes from a TRX file. Only names and outcomes are read;
    /// a claimed pass is never evidence on its own.
    let readTrxClaims (xml: string) : Result<TestClaim list, string> =
        try
            let doc = XDocument.Parse xml
            doc.Descendants()
            |> Seq.filter (fun e -> e.Name.LocalName = "UnitTestResult")
            |> Seq.map (fun e ->
                let attr (name: string) = match e.Attribute(XName.Get name) with | null -> "" | a -> a.Value
                { Id = attr "testName"; Outcome = (attr "outcome").ToLowerInvariant(); Source = "trx" })
            |> Seq.toList
            |> Ok
        with :? Xml.XmlException as ex -> Error("claimed results: malformed TRX: " + ex.Message)

    /// `Key: value` trailers of a commit message; later duplicates win.
    let trailers (message: string) =
        message.Replace("\r\n", "\n").Split('\n')
        |> Array.choose (fun line ->
            let m = Regex.Match(line, @"^(Construction-[A-Za-z]+):\s*(.*)$")
            if m.Success then Some(m.Groups[1].Value, m.Groups[2].Value.Trim()) else None)
        |> Map.ofArray

    let isProjectFile (path: string) =
        let name = IO.Path.GetFileName path
        path.EndsWith(".fsproj", StringComparison.OrdinalIgnoreCase)
        || path.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)
        || path.EndsWith(".vbproj", StringComparison.OrdinalIgnoreCase)
        || path.EndsWith(".props", StringComparison.OrdinalIgnoreCase)
        || path.EndsWith(".targets", StringComparison.OrdinalIgnoreCase)
        || name = "packages.config" || name = "paket.dependencies" || name = "paket.references"

    /// Package and project references declared in one project file. A file
    /// that is not XML yields its raw lines, so a change is still visible.
    let references (path: string) (content: string) : DependencyReference list =
        try
            XDocument.Parse(content).Descendants()
            |> Seq.choose (fun e ->
                let attr (name: string) = match e.Attribute(XName.Get name) with | null -> "" | a -> a.Value
                match e.Name.LocalName with
                | "PackageReference" | "PackageVersion" | "package" ->
                    let id = [ attr "Include"; attr "Update"; attr "id" ] |> List.tryFind (String.IsNullOrWhiteSpace >> not) |> Option.defaultValue ""
                    let version =
                        match attr "Version" with
                        | "" ->
                            match e.Element(XName.Get("Version", e.Name.NamespaceName)) with
                            | null -> attr "version"
                            | child -> child.Value
                        | v -> v
                    Some { Project = path; Kind = "package"; Include = id; Version = version }
                | "ProjectReference" -> Some { Project = path; Kind = "project"; Include = attr "Include"; Version = "" }
                | _ -> None)
            |> Seq.toList
        with :? Xml.XmlException ->
            content.Split('\n')
            |> Array.map _.Trim()
            |> Array.filter (fun l -> l <> "" && not (l.StartsWith "#"))
            |> Array.map (fun l -> { Project = path; Kind = "package"; Include = l; Version = "" })
            |> Array.toList

    /// Every added, removed or re-versioned reference between two commits.
    let dependencyChanges (before: Map<string, string>) (after: Map<string, string>) =
        let refs files = files |> Map.toList |> List.collect (fun (p, c) -> references p c)
        let keyOf (r: DependencyReference) = r.Project, r.Kind, r.Include
        let beforeIndex = refs before |> List.map (fun r -> keyOf r, r) |> Map.ofList
        let afterIndex = refs after |> List.map (fun r -> keyOf r, r) |> Map.ofList
        [ for KeyValue(k, r) in afterIndex do
            match Map.tryFind k beforeIndex with
            | None -> { Reference = r; Change = "added" }
            | Some b when b.Version <> r.Version -> { Reference = r; Change = "changed" }
            | Some _ -> ()
          for KeyValue(k, r) in beforeIndex do
            if not (Map.containsKey k afterIndex) then { Reference = r; Change = "removed" } ]

    let isAuthorized (authorization: ConstructionAuthorization) (change: DependencyChange) =
        authorization.Dependencies
        |> List.exists (fun a ->
            a.Project = change.Reference.Project && a.Kind = change.Reference.Kind
            && a.Include = change.Reference.Include && a.Change = change.Change)
