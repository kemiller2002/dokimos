namespace Dokimos.Core.Tests

open System
open Dokimos.Domain
open Dokimos.Core

/// Canonical snapshot builders shared by Core tests.
module Fixtures =
    let snapshotWith producer id metrics findings : CanonicalSnapshot =
        { SchemaVersion = CanonicalSnapshot.CurrentSchemaVersion
          SnapshotId = id
          Repository = "r"
          Revision = id
          Ref = "main"
          CollectedAt = DateTimeOffset.UnixEpoch
          Collector = "test"
          Producer = producer
          Performance = None
          Metrics = metrics
          Findings = findings }

    let ran analyzerId : AnalyzerRun =
        { AnalyzerId = analyzerId; Version = "1"; State = AnalyzerRan; DurationMilliseconds = None }

    let notRun analyzerId : AnalyzerRun =
        { AnalyzerId = analyzerId; Version = "1"; State = AnalyzerNotRun "input not supplied"; DurationMilliseconds = None }

    let producer runs : SnapshotProducer =
        { DokimosVersion = "test"; ConfigurationId = "test"; AnalyzedScope = [ "src" ]; Analyzers = runs }

    let completeProducer = producer (Capabilities.analyzers |> List.map (fun a -> ran a.AnalyzerId))

    let snapshot id metrics findings = snapshotWith (Some completeProducer) id metrics findings

    let metricAt scope id value = CanonicalMetric.available id scope "count" value "test"

    let metric id value = metricAt "A.fs" id value

    let repositoryMetric id value = metricAt CanonicalSnapshot.RepositoryScope id value

    let finding id : CanonicalFinding =
        { FindingId = id; Scope = "A.fs"; Kind = "maintainability-hotspot"; Evidence = [ StructuralComplexity 12; FrequentChange 6 ]; Explanation = "e" }
