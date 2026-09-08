using System.Text.Json;

namespace ConnectorWatch;

/// <summary>Offline lifecycle fixtures. They exercise the contract without
/// touching daemon wiring, control protocol, files, or the GUI.</summary>
public static class ReferenceLifecycleTests
{
    public static void Run()
    {
        var now = new DateTimeOffset(2026, 9, 9, 12, 0, 0, TimeSpan.Zero);
        var identity = Identity(AnalysisLoadSource.CONNECTOR_POWER);
        var candidate = Candidate(identity, now, 12.1, 12.0);
        var lifecycle = new ReferenceLifecycle(identity, now);

        var submitted = lifecycle.SetCandidate(candidate);
        Check(submitted.Succeeded && lifecycle.State == ReferenceLifecycleState.REFERENCE_UNVERIFIED,
            "candidate remains unverified before explicit acceptance");
        Check(lifecycle.Accepted is null && !lifecycle.Snapshot().KnownHealthy,
            "learning and unverified do not imply known health");

        var accepted = lifecycle.AcceptCandidate(now.AddMinutes(1), "operator",
            "reviewed fixture");
        Check(accepted.Succeeded && lifecycle.State == ReferenceLifecycleState.REFERENCE_ACCEPTED,
            "explicit candidate acceptance");
        var frozen = lifecycle.Accepted!;
        var frozenValue = frozen.Bins[425].ReferenceVolts;
        var replacement = Candidate(identity, now.AddMinutes(2), 11.7, 11.6);
        Check(lifecycle.SetCandidate(replacement).Succeeded,
            "new candidate can be observed after acceptance");
        Check(lifecycle.Accepted!.Bins[425].ReferenceVolts == frozenValue,
            "accepted reference remains frozen while candidate changes");
        Check(lifecycle.Accepted.IsFrozen && !lifecycle.Accepted.KnownHealthy,
            "accepted does not certify known healthy");

        var archived = lifecycle.ArchiveAccepted(now.AddMinutes(3), "operator",
            ReferenceArchiveReason.EXPLICIT, "fixture archive");
        Check(archived.Reference == frozen && lifecycle.Accepted is null &&
            lifecycle.State == ReferenceLifecycleState.REFERENCE_UNVERIFIED,
            "explicit archive removes accepted model and retains archive");
        Check(lifecycle.Archived.Count == 1 && lifecycle.Candidate is null,
            "archive clears candidate and preserves archive history");

        var staleLifecycle = new ReferenceLifecycle(identity, now);
        Check(staleLifecycle.SetCandidate(candidate).Succeeded &&
            staleLifecycle.AcceptCandidate(now, "operator").Succeeded,
            "fixture accepted before stale transition");
        staleLifecycle.MarkStale(now.AddMinutes(4), "source degraded at startup");
        Check(staleLifecycle.State == ReferenceLifecycleState.REFERENCE_STALE &&
            staleLifecycle.Compatibility == ReferenceCompatibility.DEGRADED &&
            staleLifecycle.Accepted is not null && !staleLifecycle.Snapshot().CanAnalyze,
            "degraded source marks accepted reference stale without deleting it");
        staleLifecycle.MarkInvalid(now.AddMinutes(5), "critical identity mismatch");
        Check(staleLifecycle.State == ReferenceLifecycleState.REFERENCE_INVALID &&
            staleLifecycle.Compatibility == ReferenceCompatibility.MISMATCH,
            "critical mismatch marks reference invalid");

        var acceptedLifecycle = new ReferenceLifecycle(identity, now);
        Check(acceptedLifecycle.SetCandidate(candidate).Succeeded &&
            acceptedLifecycle.AcceptCandidate(now, "operator").Succeeded,
            "fixture accepted for persistence");
        var persistedJson = ReferencePersistence.Serialize(acceptedLifecycle);
        Check(persistedJson.Contains("REFERENCE_ACCEPTED", StringComparison.Ordinal) &&
            persistedJson.Contains("CONNECTOR_POWER", StringComparison.Ordinal),
            "versioned persistence uses stable lifecycle and source wire names");
        var restoredDocument = ReferencePersistence.Deserialize(persistedJson);
        Check(restoredDocument.SchemaVersion == ReferencePersistence.CurrentSchemaVersion,
            "versioned persistence schema round-trip");

        var restarted = ReferencePersistence.Load(persistedJson, identity,
            now.AddHours(1), new ReferenceStartupContext(IsRestart: true));
        Check(restarted.Compatibility == ReferenceCompatibility.RESTART &&
            restarted.Lifecycle.State == ReferenceLifecycleState.REFERENCE_ACCEPTED &&
            restarted.Lifecycle.Snapshot().CanAnalyze,
            "restart preserves a matching accepted model while labeling restart");
        var degraded = ReferencePersistence.Load(persistedJson, identity,
            now.AddHours(1), new ReferenceStartupContext(IsDegraded: true));
        Check(degraded.Compatibility == ReferenceCompatibility.DEGRADED &&
            degraded.Lifecycle.State == ReferenceLifecycleState.REFERENCE_STALE,
            "degraded startup does not auto-accept or use the model");
        Check(!degraded.Lifecycle.AcceptCandidate(now.AddHours(1), "operator").Succeeded,
            "degraded source cannot accept a candidate");
        var recovered = ReferencePersistence.Load(ReferencePersistence.Serialize(degraded.Lifecycle),
            identity, now.AddHours(2), new ReferenceStartupContext(IsRestart: true));
        Check(recovered.Lifecycle.State == ReferenceLifecycleState.REFERENCE_ACCEPTED &&
            recovered.Lifecycle.Snapshot().CanAnalyze,
            "matching healthy restart recovers a previously stale accepted model");

        var mismatchIdentity = Identity(AnalysisLoadSource.NVML_BOARD_POWER);
        var mismatch = ReferencePersistence.Load(persistedJson, mismatchIdentity, now);
        Check(mismatch.Compatibility == ReferenceCompatibility.MISMATCH &&
            mismatch.Lifecycle.State == ReferenceLifecycleState.REFERENCE_INVALID &&
            mismatch.Lifecycle.Accepted is not null,
            "critical load-source mismatch requires explicit migration/archive");
        Check(identity.MatchesCritical(Identity(AnalysisLoadSource.CONNECTOR_POWER)),
            "presentation-only settings are absent from reference identity");

        var wrongCandidate = Candidate(mismatchIdentity, now, 12.1, 12.0);
        var wrongSubmission = acceptedLifecycle.SetCandidate(wrongCandidate);
        Check(!wrongSubmission.Succeeded && acceptedLifecycle.Accepted!.Bins[425].ReferenceVolts == 12.1,
            "candidate identity mismatch is rejected without changing frozen model");

        var acknowledgementState = acceptedLifecycle.State;
        var acknowledgement = acceptedLifecycle.AcknowledgeIncident("incident-1",
            "BASELINE_SHIFT", now.AddMinutes(6), "operator", "seen");
        Check(acknowledgement.IncidentId == "incident-1" &&
            acceptedLifecycle.State == acknowledgementState &&
            acceptedLifecycle.Accepted!.Bins[425].ReferenceVolts == 12.1 &&
            acceptedLifecycle.IsIncidentAcknowledged("incident-1"),
            "incident acknowledgement is separate from reference acceptance");
        Check(acceptedLifecycle.ClearIncidentAcknowledgement("incident-1") &&
            !acceptedLifecycle.IsIncidentAcknowledged("incident-1"),
            "incident acknowledgement can be cleared independently");

        var legacyJson = JsonSerializer.Serialize(new
        {
            Identity = "v4|legacy-source|old-profile",
            Bins = new Dictionary<int, object>
            {
                [425] = new { Learning = Array.Empty<double>(), Reference = 12.1, ReferenceP05 = 12.0 },
            },
        });
        var migration = ReferencePersistence.MigrateLegacyBaseline(legacyJson, identity, now);
        Check(migration.Compatibility == ReferenceCompatibility.LEGACY &&
            migration.RequiresExplicitMigration && migration.Candidate?.Origin == ReferenceCandidateOrigin.LEGACY_MIGRATION &&
            migration.Document.Accepted is null &&
            migration.Document.State == ReferenceLifecycleState.REFERENCE_UNVERIFIED,
            "legacy baseline migrates to an unverified candidate without auto-acceptance");
        var legacyLoaded = ReferencePersistence.Load(legacyJson, identity, now);
        Check(legacyLoaded.Compatibility == ReferenceCompatibility.LEGACY &&
            legacyLoaded.Lifecycle.Compatibility == ReferenceCompatibility.LEGACY &&
            legacyLoaded.Lifecycle.State == ReferenceLifecycleState.REFERENCE_UNVERIFIED &&
            legacyLoaded.RequiresExplicitMigration && legacyLoaded.Lifecycle.Candidate is not null,
            "legacy load remains visibly unverified");
        Check(!legacyLoaded.Lifecycle.AcceptCandidate(now, "operator").Succeeded,
            "legacy candidate cannot be accepted without explicit migration acknowledgement");
        Check(legacyLoaded.Lifecycle.AcceptCandidate(now, "operator",
                explicitLegacyMigration: true).Succeeded &&
            legacyLoaded.Lifecycle.State == ReferenceLifecycleState.REFERENCE_ACCEPTED,
            "explicit legacy migration may accept after review");
        var legacyRoundTrip = ReferencePersistence.Load(
            ReferencePersistence.Serialize(ReferencePersistence.Load(legacyJson, identity, now).Lifecycle),
            identity, now.AddMinutes(1));
        Check(legacyRoundTrip.RequiresExplicitMigration &&
            legacyRoundTrip.Lifecycle.Candidate?.Origin == ReferenceCandidateOrigin.LEGACY_MIGRATION,
            "legacy migration requirement survives lifecycle persistence");

        var invalid = ReferencePersistence.Load("{not-json", identity, now);
        Check(invalid.Lifecycle.State == ReferenceLifecycleState.REFERENCE_INVALID &&
            invalid.Compatibility == ReferenceCompatibility.MISMATCH,
            "invalid persisted model is not silently treated as healthy");
        var missing = ReferencePersistence.Load(null, identity, now);
        Check(missing.Lifecycle.State == ReferenceLifecycleState.REFERENCE_UNVERIFIED &&
            missing.Compatibility == missing.Lifecycle.Compatibility &&
            missing.Compatibility == ReferenceCompatibility.LEGACY,
            "missing persisted model is visibly unverified");

        var runtimeConfig = new Config
        {
            GpuUuid = identity.GpuUuid,
            StableSamples = 1,
            BaselineSamples = 5,
            WindowSamples = 3,
            SustainSamples = 2,
        };
        var runtimeAnalysis = new Analysis(runtimeConfig);
        var runtimeAt = now;
        for (int i = 0; i < 8; i++)
            _ = runtimeAnalysis.Add(runtimeAt = runtimeAt.AddSeconds(1), 440, 12.1);
        Check(runtimeAnalysis.Bins[425].Reference is null &&
            runtimeAnalysis.Bins[425].CandidateReference is 12.1,
            "analysis publishes a candidate without auto-acceptance");
        var runtimeIdentity = Program.BuildReferenceIdentity(runtimeConfig, "json",
            "fixture rail source", AnalysisLoadSource.CONNECTOR_POWER);
        var runtimeCandidate = runtimeAnalysis.BuildCandidate(runtimeIdentity, runtimeAt);
        Check(runtimeCandidate is not null && runtimeCandidate.IsQualified,
            "analysis candidate snapshot is qualified");
        var runtimeLifecycle = new ReferenceLifecycle(runtimeIdentity, runtimeAt);
        Check(runtimeLifecycle.SetCandidate(runtimeCandidate!).Succeeded &&
            runtimeLifecycle.AcceptCandidate(runtimeAt.AddSeconds(1), "operator").Succeeded,
            "runtime candidate requires and supports explicit acceptance");
        runtimeAnalysis.ApplyAccepted(runtimeLifecycle.Accepted!);
        Check(runtimeAnalysis.Bins[425].Reference == 12.1 &&
            runtimeAnalysis.Bins[425].CandidateReference is null,
            "explicit acceptance is the only promotion into analysis");
        var mapped = Program.BuildAnalysisBins(runtimeLifecycle, new Saved("legacy",
            new Dictionary<int, Bin> { [425] = new() { Reference = 99, Learning = [1, 2] } }));
        Check(mapped[425].Reference == 12.1 && mapped[425].Learning.Count == 0,
            "typed accepted model overrides legacy mirror on restart");

        Console.WriteLine("PASS: reference lifecycle, identity, migration and freeze fixtures.");
    }

    static ReferenceIdentity Identity(AnalysisLoadSource loadSource) =>
        ReferenceIdentity.Create(
            gpuUuid: "GPU-00000000-0000-0000-0000-000000000001",
            board: "ASUS-TUF-RTX-5090",
            driver: "616.56",
            source: "direct NVIDIA rails",
            abiProfile: "A612-A613-v1",
            analysisLoadSource: loadSource,
            featureVersion: "electrical-v1",
            modelVersion: "trend-v1",
            schemaVersion: 3,
            qualification: new ReferenceQualificationConfig(
                binWatts: 25,
                minAnalysisWatts: 100,
                stableSamples: 5,
                baselineSamples: 20,
                windowSamples: 10,
                windowMaxAgeSeconds: 1800,
                maxAgeSeconds: 5,
                sampleSeconds: 1));

    static ReferenceCandidateModel Candidate(ReferenceIdentity identity,
        DateTimeOffset atUtc, double referenceVolts, double p05Volts) =>
        ReferenceCandidateModel.Learned(identity,
            new Dictionary<int, ReferenceBinStatistics>
            {
                [425] = new ReferenceBinStatistics(425, referenceVolts, p05Volts,
                    learningSamples: 0, observedSamples: 20),
            },
            qualifiedSamples: 20,
            requiredSamples: 20,
            firstObservedAtUtc: atUtc.AddMinutes(-1),
            lastObservedAtUtc: atUtc,
            isQualified: true,
            detail: "fixture candidate");

    static void Check(bool condition, string name)
    {
        if (!condition) throw new Exception("FAILED: " + name);
    }
}
