using System.Text.Json;
using System.Text.Json.Nodes;

namespace ConnectorWatch;

public static class AutoAcceptReferenceTests
{
    public static void Run()
    {
        var now = new DateTimeOffset(2026, 9, 15, 12, 0, 0, TimeSpan.Zero);
        var identity = Identity();
        var qualified = Candidate(identity, now, isQualified: true);
        var incomplete = Candidate(identity, now, isQualified: false);

        Check(new Config().AutoAcceptReference,
            "automatic reference acceptance defaults on for new configurations");
        Check(JsonSerializer.Deserialize<Config>("{}")!.AutoAcceptReference,
            "missing policy field upgrades a pre-1.5.1 configuration to enabled");
        Check(JsonSerializer.Deserialize<Config>("{\"AutoAcceptReference\":true}")!.AutoAcceptReference,
            "explicitly enabled policy survives deserialization");
        Check(!JsonSerializer.Deserialize<Config>("{\"AutoAcceptReference\":false}")!.AutoAcceptReference,
            "explicitly disabled policy survives deserialization");
        var configPath = Path.Combine(Path.GetTempPath(),
            "ConnectorWatch-auto-config-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            File.WriteAllText(configPath,
                "{\n  \"AutoAcceptReference\": false,\n  \"future_setting\": { \"keep\": 7 }\n}");
            ConfigPersistence.SetAutoAcceptReference(configPath, true);
            var enabled = JsonNode.Parse(File.ReadAllText(configPath))!.AsObject();
            Check(enabled["AutoAcceptReference"]?.GetValue<bool>() == true &&
                enabled["future_setting"]?["keep"]?.GetValue<int>() == 7,
                "live policy persistence preserves unknown configuration fields");
            ConfigPersistence.SetAutoAcceptReference(configPath, false);
            var disabled = JsonNode.Parse(File.ReadAllText(configPath))!.AsObject();
            Check(disabled["AutoAcceptReference"]?.GetValue<bool>() == false,
                "live policy persistence can disable automatic acceptance");

            File.WriteAllText(configPath,
                "{\"auto_accept_reference\":false,\"future_setting\":{\"keep\":7}}");
            ConfigPersistence.SetAutoAcceptReference(configPath, true);
            var restartedConfig = JsonSerializer.Deserialize<Config>(
                File.ReadAllText(configPath));
            Check(restartedConfig?.AutoAcceptReference == true &&
                JsonNode.Parse(File.ReadAllText(configPath))!["future_setting"]?["keep"]?.GetValue<int>() == 7,
                "persisted policy uses the startup configuration contract after restart");
        }
        finally
        {
            try { File.Delete(configPath); } catch { }
        }

        var lifecycle = new ReferenceLifecycle(identity, now);
        lifecycle.SetCandidate(qualified);
        Check(ReferenceAutoAcceptance.CanAccept(lifecycle, enabled: true,
                sourceHealthy: true, out _),
            "qualified learned candidate passes automatic acceptance gates");

        var fresh = ReferencePersistence.Load(null, identity, now).Lifecycle;
        fresh.SetCandidate(qualified);
        Check(ReferenceAutoAcceptance.CanAccept(fresh, enabled: true,
                sourceHealthy: true, out _),
            "fresh learned candidate is eligible when empty startup is labeled legacy");
        Check(!ReferenceAutoAcceptance.CanAccept(lifecycle, enabled: true,
                sourceHealthy: false, out _),
            "degraded source blocks automatic acceptance");
        Check(!ReferenceAutoAcceptance.CanAccept(lifecycle, enabled: false,
                sourceHealthy: true, out _),
            "disabled policy blocks automatic acceptance");

        var incompleteLifecycle = new ReferenceLifecycle(identity, now);
        incompleteLifecycle.SetCandidate(incomplete);
        Check(!ReferenceAutoAcceptance.CanAccept(incompleteLifecycle, enabled: true,
                sourceHealthy: true, out _),
            "incomplete candidate cannot be automatically accepted");

        string persisted = ReferencePersistence.Serialize(lifecycle);
        var restarted = ReferencePersistence.Load(persisted, identity, now.AddMinutes(1),
            new ReferenceStartupContext(IsRestart: true));
        Check(restarted.Compatibility == ReferenceCompatibility.RESTART &&
            restarted.Lifecycle.Candidate?.Origin == ReferenceCandidateOrigin.LEARNED &&
            restarted.Lifecycle.Candidate.IsQualified &&
            ReferenceAutoAcceptance.CanAccept(restarted.Lifecycle, enabled: true,
                sourceHealthy: true, out _),
            "qualified learned candidate survives restart for later automatic acceptance");

        var degraded = ReferencePersistence.Load(persisted, identity, now.AddMinutes(1),
            new ReferenceStartupContext(IsDegraded: true));
        Check(!ReferenceAutoAcceptance.CanAccept(degraded.Lifecycle, enabled: true,
                sourceHealthy: true, out _),
            "degraded persisted candidate remains blocked");

        var invalid = new ReferenceLifecycle(identity, now);
        invalid.SetCandidate(qualified);
        invalid.MarkInvalid(now.AddMinutes(1), "identity mismatch");
        Check(!ReferenceAutoAcceptance.CanAccept(invalid, enabled: true,
                sourceHealthy: true, out _),
            "invalid lifecycle cannot be automatically accepted");

        var legacyJson = "{\"Identity\":\"legacy\",\"Bins\":{\"425\":{\"Reference\":12.1,\"ReferenceP05\":12.0}}}";
        var legacy = ReferencePersistence.Load(legacyJson, identity, now);
        Check(legacy.Lifecycle.Candidate?.Origin == ReferenceCandidateOrigin.LEGACY_MIGRATION &&
            !ReferenceAutoAcceptance.CanAccept(legacy.Lifecycle, enabled: true,
                sourceHealthy: true, out _),
            "legacy candidate cannot be implicitly migrated");

        var accepted = new ReferenceLifecycle(identity, now);
        accepted.SetCandidate(qualified);
        accepted.AcceptCandidate(now.AddMinutes(1), "operator");
        accepted.SetCandidate(Candidate(identity, now.AddMinutes(2), isQualified: true,
            referenceVolts: 11.8));
        Check(!ReferenceAutoAcceptance.CanAccept(accepted, enabled: true,
                sourceHealthy: true, out _),
            "automatic acceptance cannot replace an accepted baseline");

        var analysis = new Analysis(new Config
        {
            StableSamples = 1,
            BaselineSamples = 5,
            WindowSamples = 3,
            SustainSamples = 1,
        });
        analysis.Bins[425] = new Bin { CandidateReference = 12.1, CandidateReferenceP05 = 12.0 };
        var acceptedLifecycle = new ReferenceLifecycle(identity, now);
        acceptedLifecycle.SetCandidate(qualified);
        Check(acceptedLifecycle.AcceptCandidate(now.AddMinutes(1), "auto").Succeeded,
            "automatic acceptance uses normal lifecycle acceptance");
        analysis.ApplyAccepted(acceptedLifecycle.Accepted!);
        Check(analysis.Bins[425].Reference == 12.1 &&
            analysis.Bins[425].CandidateReference is null &&
            analysis.Bins[425].Learning.Count == 0,
            "automatic acceptance applies frozen values and clears candidate learning");

        var differentialIdentity = new DifferentialModelIdentity(
            gpuUuid: "GPU-test", board: "board", driver: "driver",
            voltageSource: "source", configurationId: "config");
        var differentialRuntime = new DifferentialModelRuntime(
            new DifferentialModelOptions
            {
                Identity = differentialIdentity,
                LoadProxy = DifferentialLoadProxy.CONNECTOR_POWER,
                IncludeBoardPower = false,
                IncludeTemperature = false,
                MinimumSamples = 2,
                MinimumSlopeSamples = 2,
                MinimumLoadSpan = 1,
            },
            new ResidualDetectorOptions
            {
                ExpectedIdentity = differentialIdentity.CanonicalKey,
            },
            maximumLearningSamples: 2);
        var automaticAttempt = new ReferenceLifecycle(identity, now);
        automaticAttempt.SetCandidate(qualified);
        long lastPreviewGeneration = -1;
        var firstAttempt = ReferenceAutoAcceptance.TryAccept(automaticAttempt,
            enabled: true, sourceHealthy: true, evidenceGeneration: 0,
            ref lastPreviewGeneration, minimumRetrySamples: 2,
            preview: () => differentialRuntime.PreviewFit(now),
            accept: () => automaticAttempt.AcceptCandidate(now, "auto"),
            out var waitingDetail);
        Check(!firstAttempt && automaticAttempt.Accepted is null &&
            waitingDetail.Contains("usable differential model", StringComparison.Ordinal),
            "automatic acceptance retains a qualified candidate while fit evidence is incomplete");

        for (int i = 0; i < 3; i++)
        {
            var sampleTime = now.AddSeconds(i + 1);
            differentialRuntime.Observe(new DifferentialSample(sampleTime,
                12.0 + i * .01,
                new DifferentialFeatureVector(connectorPowerW: 100 + i),
                identity: differentialIdentity.CanonicalKey), candidateLearning: true);
        }
        Check(differentialRuntime.LearningSampleCount == 2 &&
            differentialRuntime.LearningSampleGeneration == 3,
            "automatic fit retry evidence generation survives bounded sample eviction");

        var acceptedAttempt = ReferenceAutoAcceptance.TryAccept(automaticAttempt,
            enabled: true, sourceHealthy: true,
            evidenceGeneration: differentialRuntime.LearningSampleGeneration,
            ref lastPreviewGeneration, minimumRetrySamples: 2,
            preview: () => differentialRuntime.PreviewFit(now.AddMinutes(1)),
            accept: () => automaticAttempt.AcceptCandidate(now.AddMinutes(1), "auto"),
            out var acceptedDetail);
        Check(acceptedAttempt && automaticAttempt.Accepted is not null &&
            acceptedDetail.Contains("Automatically accepted", StringComparison.Ordinal),
            "automatic acceptance uses the shared lifecycle pipeline after usable fit evidence");
        var acceptedRestart = ReferencePersistence.Load(
            ReferencePersistence.Serialize(automaticAttempt), identity,
            now.AddMinutes(2), new ReferenceStartupContext(IsRestart: true));
        Check(acceptedRestart.Lifecycle.Snapshot().CanAnalyze &&
            acceptedRestart.Lifecycle.Accepted is not null,
            "automatically accepted reference persists as analyzable across restart");

        Console.WriteLine("PASS: automatic reference acceptance gates and persistence fixtures.");
    }

    static ReferenceIdentity Identity() => ReferenceIdentity.Create(
        gpuUuid: "GPU-00000000-0000-0000-0000-000000000001",
        board: "ASUS-TUF-RTX-5090",
        driver: "616.56",
        source: "direct NVIDIA rails",
        abiProfile: "A612-A613-v1",
        analysisLoadSource: AnalysisLoadSource.CONNECTOR_POWER,
        featureVersion: "electrical-v1",
        modelVersion: "trend-v1",
        schemaVersion: 3,
        qualification: new ReferenceQualificationConfig(
            binWatts: 25, minAnalysisWatts: 100, stableSamples: 1,
            baselineSamples: 5, windowSamples: 3, windowMaxAgeSeconds: 1800,
            maxAgeSeconds: 5, sampleSeconds: 1));

    static ReferenceCandidateModel Candidate(ReferenceIdentity identity,
        DateTimeOffset atUtc, bool isQualified, double referenceVolts = 12.1) =>
        ReferenceCandidateModel.Learned(identity,
            new Dictionary<int, ReferenceBinStatistics>
            {
                [425] = new(425, isQualified ? referenceVolts : null,
                    isQualified ? 12.0 : null, learningSamples: 0, observedSamples: 20),
            }, qualifiedSamples: isQualified ? 20 : 2, requiredSamples: 20,
            firstObservedAtUtc: atUtc.AddMinutes(-1), lastObservedAtUtc: atUtc,
            isQualified: isQualified, detail: "fixture candidate");

    static void Check(bool condition, string name)
    {
        if (!condition) throw new Exception("FAILED: " + name);
    }
}
