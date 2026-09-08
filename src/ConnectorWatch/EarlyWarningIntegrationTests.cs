namespace ConnectorWatch;

/// <summary>Hardware-free checks for the daemon's #10-#12 integration seam.
/// This file is intentionally not added to the legacy self-test dispatcher;
/// it can be invoked by an integration test host without starting NVML.</summary>
public static class EarlyWarningIntegrationTests
{
    public static void Run()
    {
        RuntimeEvidenceGates();
        var identity = new DifferentialModelIdentity(
            gpuUuid: "GPU-INTEGRATION",
            board: "BOARD-INTEGRATION",
            driver: "DRIVER-INTEGRATION",
            voltageSource: "source-integration",
            configurationId: "configuration-integration");
        var options = new DifferentialModelOptions
        {
            LoadProxy = DifferentialLoadProxy.CONNECTOR_POWER,
            IncludeBoardPower = false,
            IncludeTemperature = false,
            MinimumSamples = 3,
            MinimumSlopeSamples = 3,
            MinimumLoadSpan = 10,
            EnvelopeMarginFraction = 0,
            Identity = identity,
        };
        var runtime = new DifferentialModelRuntime(options, new ResidualDetectorOptions
        {
            FastDroopThresholdVolts = .1,
            EwmaDroopThresholdVolts = .1,
            EwmaRecoveryThresholdVolts = .02,
            EwmaConfirmationSamples = 2,
            ExpectedIdentity = identity.CanonicalKey,
        });
        var start = new DateTimeOffset(2026, 9, 9, 0, 0, 0, TimeSpan.Zero);
        DifferentialSample Sample(int index, double voltage, bool settled = true,
            double? loadWatts = null) =>
            new(start.AddSeconds(index), voltage,
                new DifferentialFeatureVector(connectorPowerW: loadWatts ?? 200 + index * 50),
                ageSeconds: .1, voltageTimestampUtc: start.AddSeconds(index),
                featureTimestampUtc: start.AddSeconds(index), isSettled: settled,
                identity: identity.CanonicalKey);

        var rejected = runtime.Observe(Sample(0, 12, settled: false), candidateLearning: true);
        Check(!rejected.LearningSampleAdmitted && runtime.LearningSampleCount == 0,
            "unsettled candidate sample is not accumulated");
        runtime.Observe(Sample(1, 12), candidateLearning: true);
        runtime.Observe(Sample(2, 11.95), candidateLearning: true);
        runtime.Observe(Sample(3, 11.9), candidateLearning: true);
        Check(runtime.LearningSampleCount == 3,
            "only qualified candidate samples are retained");

        var fit = runtime.FitAndFreeze(start.AddSeconds(4));
        Check(fit.IsUsable && runtime.Artifact is not null,
            "explicit acceptance freezes a usable differential artifact");
        var alert = runtime.Observe(Sample(4, 11.5, loadWatts: 300), candidateLearning: false);
        Check(alert.Prediction.IsAvailable && alert.Prediction.ResidualVolts < -.1 &&
            alert.Detector.IsAlert, "frozen residual drives the composite detector");

        var ledger = new IncidentLedger(identity.CanonicalKey,
            new IncidentCaptureOptions { PreSamples = 2, PostSamples = 2 });
        ledger.Observe(new IncidentObservation(start, "NO_SHIFT_DETECTED", 12, 200));
        var latched = ledger.Observe(new IncidentObservation(start.AddSeconds(1),
            "SUDDEN_DROOP", 11.5, 350), new IncidentTrigger(
                "fast-residual", "SUDDEN_DROOP", IncidentSeverity.CRITICAL));
        Check(latched.Triggered && latched.Incident is not null,
            "residual alert is latched with a bounded incident");
        var restored = IncidentLedger.Load(ledger.ToJson(), identity.CanonicalKey);
        var incidentId = latched.Incident!.IncidentId;
        restored.Acknowledge(incidentId, start.AddSeconds(2), "integration-test");
        Check(restored.Get(incidentId)!.State == IncidentLifecycleState.ACKNOWLEDGED,
            "acknowledgement remains separate from resolution");

        using var stop = new CancellationTokenSource();
        using var server = new ControlServer(Path.Combine(Path.GetTempPath(),
            "ConnectorWatch-integration-" + Guid.NewGuid().ToString("N")), stop,
            instanceId: "integration-instance");
        server.IncidentCommand = request =>
        {
            restored.Resolve(request.IncidentId!, start.AddSeconds(3), request.ClientId);
            return new ControlCommandResult(true, "resolved", IncidentId: request.IncidentId,
                IncidentState: restored.Get(request.IncidentId!)!.State.WireName());
        };
        var response = server.HandleRequest(new ControlRequest("resolve-incident",
            "integration-client", server.InstanceId, IncidentId: incidentId));
        Check(response.Ok && response.IncidentId == incidentId &&
            response.IncidentState == "RESOLVED", "identity-bound incident resolution command");
    }

    static void RuntimeEvidenceGates()
    {
        var start = new DateTimeOffset(2026, 9, 9, 4, 0, 0, TimeSpan.Zero);
        var fresh = FreshnessMetadata.HostPoll(start, "host poll only");
        var typed = ElectricalSample.FromLegacy(
            new Voltage(start, 12.1, 440, "{}"), "fixture", fresh);
        var stale = typed with { Freshness = FreshnessMetadata.Unavailable(
            start, "fixture source gap") };
        Check(!Program.IsSourceDegraded(typed, typed.ToLegacyVoltage(), null),
            "fresh host-poll source is not treated as a source gap");
        Check(Program.IsSourceDegraded(stale, stale.ToLegacyVoltage(), null),
            "stale electrical source is propagated as degradation");
        Check(Program.HasAdvancingSensorTimestamp(null, start) &&
            !Program.HasAdvancingSensorTimestamp(start, start) &&
            !Program.HasAdvancingSensorTimestamp(start, start.AddSeconds(-1)),
            "repeated and backwards sensor timestamps are not advancing");

        var config = new Config { GpuUuid = "GPU-RUNTIME-EVIDENCE" };
        var referenceIdentity = Program.BuildReferenceIdentity(config, "json",
            "fixture-source", AnalysisLoadSource.CONNECTOR_POWER);
        var candidate = ReferenceCandidateModel.Learned(referenceIdentity,
            new Dictionary<int, ReferenceBinStatistics>
            {
                [425] = new(425, 12.1, 12.0, observedSamples: 5),
            }, qualifiedSamples: 5, requiredSamples: 5,
            firstObservedAtUtc: start, lastObservedAtUtc: start,
            isQualified: true, detail: "runtime fixture");
        var lifecycle = new ReferenceLifecycle(referenceIdentity, start);
        Check(lifecycle.SetCandidate(candidate).Succeeded &&
            lifecycle.AcceptCandidate(start, "runtime-test").Succeeded,
            "runtime fixture starts with an accepted reference");
        Check(!Program.MarkReferenceStaleIfSourceDegraded(lifecycle, typed,
            typed.ToLegacyVoltage(), null, start, "fresh source"),
            "healthy source leaves accepted reference usable");
        Check(Program.MarkReferenceStaleIfSourceDegraded(lifecycle, stale,
            stale.ToLegacyVoltage(), null, start.AddSeconds(1), "fixture source gap") &&
            lifecycle.State == ReferenceLifecycleState.REFERENCE_STALE &&
            lifecycle.Compatibility == ReferenceCompatibility.DEGRADED,
            "runtime source degradation marks the accepted reference stale");

        var observation = Program.BuildPowerLimitObservation(start,
            new Gpu(440, 60, 90, 450), "runtime-watchdog");
        Check(!observation.FreshnessVerified && observation.SourceTimestampUtc is null &&
            observation.ConfiguredLimitWatts is null,
            "watchdog observation does not invent verified or configured evidence");
        var watchdog = new PowerLimitWatchdog(
            new PowerLimitWatchdogOptions(BaselineSamples: 1), "runtime-watchdog");
        var watchdogResult = watchdog.Observe(observation);
        Check(watchdogResult.Status == PowerLimitWatchdogStatus.FreshnessUnverified &&
            !watchdogResult.LimitAvailable &&
            watchdogResult.DifferenceFromConfiguredWatts is null,
            "watchdog stays fail-closed without source freshness and a configured pair");
    }

    static void Check(bool condition, string detail)
    {
        if (!condition) throw new Exception("FAILED: " + detail);
    }
}
