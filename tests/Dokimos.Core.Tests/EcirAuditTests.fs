module Dokimos.Core.Tests.EcirAuditTests

open System
open System.Security.Cryptography
open System.Text
open System.Text.Json
open Xunit
open Dokimos.Core

let private key = "requirements/spec.md#R-1"
let private fields =
    [ key; "R-1"; "requirements/spec.md"; "L10"; "sha256:revision"; "sha256:content" ]

let private digest () =
    let builder = StringBuilder("ecir-source-manifest/1\n")
    for value in fields do
        builder.Append(Encoding.UTF8.GetByteCount value).Append(':').Append(value) |> ignore
    let bytes = Encoding.UTF8.GetBytes(builder.ToString()) |> SHA256.HashData
    "sha256:" + (Convert.ToHexString bytes).ToLowerInvariant()

let private source = """{"key":"requirements/spec.md#R-1","originalId":"R-1","document":"requirements/spec.md","location":"L10","revision":"sha256:revision","contentDigest":"sha256:content"}"""
let private manifest () = sprintf """{"digest":"%s","requirements":[%s]}""" (digest ()) source

let private model nodeRefs sourceRow state =
    sprintf """{"schemaVersion":"ecir/1","sourceManifestDigest":"%s","requirements":[{"source":%s,"disposition":%s,"nodeIds":%s}],"nodes":[%s]}"""
        (digest ()) sourceRow state nodeRefs
        (sprintf """{"id":"COHORT","kind":"cohort","dependsOn":[],"requirementKeys":["%s"]},{"id":"VERIFY","kind":"verificationObligation","dependsOn":[],"requirementKeys":["%s"]}""" key key)

let private valid () = model """["COHORT","VERIFY"]""" source """{"kind":"modeled"}"""

let private read manifest blueprint =
    match EcirAudit.audit manifest blueprint with
    | Ok report -> report
    | Error issues -> failwithf "%A" issues

let private codes (report: EcirAuditReport) = report.Findings |> List.map _.Code

[<Fact>]
let ``two-layer audit preserves source coverage but never invents independently verified completions`` () =
    let report = read (manifest ()) (valid ())
    Assert.Empty report.Findings
    Assert.Equal(1, report.Imported)
    Assert.Equal(1, report.Represented)
    Assert.Equal(1, report.Modeled)
    Assert.Equal(0, report.IndependentlyVerified)

[<Fact>]
let ``independent audit catches source manifest tampering`` () =
    let bad = (manifest ()).Replace("sha256:content", "sha256:changed")
    Assert.Contains("source-manifest-digest-mismatch", codes (read bad (valid ())))

[<Fact>]
let ``independent audit catches a dropped source even if blueprint claims it has the manifest digest`` () =
    let blueprint = (valid ()).Replace("requirements/spec.md#R-1", "requirements/spec.md#R-9")
    Assert.Contains("missing-requirement", codes (read (manifest ()) blueprint))

[<Fact>]
let ``independent audit rejects orphaned verification nodes`` () =
    let blueprint = model """["COHORT"]""" source """{"kind":"modeled"}"""
    let report = read (manifest ()) blueprint
    Assert.Contains("no-verification-obligation", codes report)
    Assert.Contains("nonreciprocal-link", codes report)

[<Fact>]
let ``modeled requirement stranded outside every cohort fails independent audit`` () =
    let blueprint =
        (valid ()).Replace(
            "\"nodeIds\":[\"COHORT\",\"VERIFY\"]",
            "\"nodeIds\":[\"VERIFY\"]"
        )
    Assert.Contains("no-construction-cohort", codes (read (manifest ()) blueprint))

[<Fact>]
let ``deferred scope is separately counted and never automatically accepted`` () =
    let blueprint = model """["COHORT","VERIFY"]""" source """{"kind":"deferred","reason":"next release"}"""
    let report = read (manifest ()) blueprint
    Assert.Equal(1, report.Deferred)
    Assert.Equal(0, report.Modeled)
    Assert.Equal(0, report.IndependentlyVerified)

[<Fact>]
let ``unsupported future blueprint and invalid JSON are refusals`` () =
    Assert.Contains("unsupported-schema", codes (read (manifest ()) ((valid ()).Replace("ecir/1", "ecir/9"))))
    Assert.True(EcirAudit.audit "bad json" (valid ()) |> Result.isError)
