namespace ConnectorWatch;

/// <summary>Hardware-free checks for the daemon's #10-#12 integration seam.
/// This file is intentionally not added to the legacy self-test dispatcher;
/// it can be invoked by an integration test host without starting NVML.</summary>
public static class EarlyWarningIntegrationTests
{
    public static void Run()
    {
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

    static void Check(bool condition, string detail)
    {
        if (!condition) throw new Exception("FAILED: " + detail);
    }
}
