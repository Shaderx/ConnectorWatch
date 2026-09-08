namespace ConnectorWatch;

/// <summary>
/// Hardware-free checks for the read-only power-limit watchdog.  The suite
/// deliberately has no NVML/NVAPI calls and no test that attempts to change a
/// power limit; a caller can wire <see cref="Run"/> into the daemon self-test.
/// </summary>
public static class PowerLimitWatchdogTests
{
    public static void Run()
    {
        ReadOnlyConfirmationAndLatch();
        StaleUnavailableAndUnverifiedAreDistinct();
        TimeAndIdentityGatesResetConfirmation();
        StateRoundTripPreservesIncident();
        EnforcementTelemetryIsBoundedAndIndependent();
        ConfiguredCapChangesResetEnforcementConfirmation();
        MissingAndResetEvidenceRemainVisible();
        InvalidEvidenceCannotBridgeConfirmationOrPoisonIdentity();
    }

    static void ReadOnlyConfirmationAndLatch()
    {
        var options = new PowerLimitWatchdogOptions(
            IncreaseToleranceWatts: 1,
            DriftToleranceWatts: 2,
            MinimumConfirmationSamples: 3,
            ConfirmationSeconds: 2,
            BaselineSamples: 2,
            MaximumGapSeconds: 20,
            NearLimitMarginWatts: 5);
        var watchdog = new PowerLimitWatchdog(options, "fixture-gpu");
        var t = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        var first = watchdog.Observe(Sample(t, 450, 450, 200), 0);
        Check(first.Status == PowerLimitWatchdogStatus.BaselineLearning,
            "first limit observation learns a baseline");
        var baseline = watchdog.Observe(Sample(t.AddSeconds(1), 450, 450, 200), 1);
        Check(baseline.Status == PowerLimitWatchdogStatus.Ok &&
            baseline.BaselineLimitWatts == 450,
            "second stable observation establishes the frozen baseline");

        var pending = watchdog.Observe(Sample(t.AddSeconds(2), 450, 455, 445), 2);
        Check(pending.Status == PowerLimitWatchdogStatus.Increased &&
            pending.ConfirmationSamples == 1,
            "unexpected increase is pending instead of immediately reported");
        var pendingAgain = watchdog.Observe(Sample(t.AddSeconds(3), 450, 455, 445), 3);
        Check(pendingAgain.Status == PowerLimitWatchdogStatus.Increased &&
            pendingAgain.ConfirmationSamples == 2,
            "confirmation requires repeated observations and elapsed time");
        var confirmed = watchdog.Observe(Sample(t.AddSeconds(4), 450, 455, 445), 4);
        Check(confirmed.Status == PowerLimitWatchdogStatus.IncreasedLatched &&
            confirmed.IncidentLatched && confirmed.BoardPowerStatus == PowerLimitBoardStatus.NearLimit,
            "confirmed increase is latched and board proximity remains explicit");
        Check(!PowerLimitWatchdog.WritesPowerLimit && confirmed.IsReadOnly &&
            confirmed.NoSafetyCertification,
            "watchdog exposes a read-only, non-certifying contract");
        bool rejectedWrite = false;
        try { PowerLimitWatchdog.RejectLimitMutation(); }
        catch (NotSupportedException) { rejectedWrite = true; }
        Check(rejectedWrite, "limit mutation is rejected rather than sent to hardware");

        var stillLatched = watchdog.Observe(Sample(t.AddSeconds(5), 450, 450, 200), 5);
        Check(stillLatched.Status == PowerLimitWatchdogStatus.IncreasedLatched,
            "a confirmed incident stays latched while the reading recovers");
        watchdog.AcknowledgeIncident();
        var acknowledged = watchdog.Observe(Sample(t.AddSeconds(6), 450, 450, 200), 6);
        Check(acknowledged.Status == PowerLimitWatchdogStatus.Ok && !acknowledged.IncidentLatched,
            "only explicit acknowledgement clears the latched presentation");
    }

    static void StaleUnavailableAndUnverifiedAreDistinct()
    {
        var watchdog = new PowerLimitWatchdog(
            new PowerLimitWatchdogOptions(BaselineSamples: 1), "fixture-gpu");
        var t = new DateTimeOffset(2026, 1, 1, 1, 0, 0, TimeSpan.Zero);
        var unverified = watchdog.Observe(Sample(t, 450, 450, 100,
            freshnessVerified: false));
        Check(unverified.Status == PowerLimitWatchdogStatus.FreshnessUnverified &&
            !unverified.LimitAvailable,
            "unverified freshness is fail-closed rather than baseline evidence");
        var stale = watchdog.Observe(Sample(t.AddSeconds(1), 450, 450, 100,
            isFresh: false, freshnessVerified: true));
        Check(stale.Status == PowerLimitWatchdogStatus.Stale,
            "stale power-limit telemetry has a distinct status");
        var missing = watchdog.Observe(Sample(t.AddSeconds(2), 450, null, null,
            freshnessVerified: true));
        Check(missing.Status == PowerLimitWatchdogStatus.Unavailable &&
            missing.BoardPowerStatus == PowerLimitBoardStatus.Unavailable,
            "missing limit and board power are reported as unavailable");
    }

    static void TimeAndIdentityGatesResetConfirmation()
    {
        var options = new PowerLimitWatchdogOptions(
            MinimumConfirmationSamples: 2, ConfirmationSeconds: 0,
            BaselineSamples: 1, MaximumGapSeconds: 5);
        var t = new DateTimeOffset(2026, 1, 1, 2, 0, 0, TimeSpan.Zero);
        var watchdog = new PowerLimitWatchdog(options, "fixture-gpu");
        _ = watchdog.Observe(Sample(t, 450, 450, 200), 0);
        var pending = watchdog.Observe(Sample(t.AddSeconds(1), 450, 455, 200), 1);
        Check(pending.Status == PowerLimitWatchdogStatus.Increased,
            "increase enters pending state before confirmation");
        var gap = watchdog.Observe(Sample(t.AddSeconds(20), 450, 455, 200), 20);
        Check(gap.Status == PowerLimitWatchdogStatus.TimeDiscontinuity &&
            !gap.IncidentLatched,
            "large host/monotonic gaps reset pending confirmation");
        var wrongIdentity = watchdog.Observe(Sample(t.AddSeconds(21), 450, 450, 200,
            identity: "other-gpu"), 21);
        Check(wrongIdentity.Status == PowerLimitWatchdogStatus.IdentityMismatch,
            $"identity mismatch never enters the baseline or incident path (actual: {wrongIdentity.Status})");
    }

    static void StateRoundTripPreservesIncident()
    {
        var options = new PowerLimitWatchdogOptions(
            MinimumConfirmationSamples: 1, BaselineSamples: 1);
        var t = new DateTimeOffset(2026, 1, 1, 3, 0, 0, TimeSpan.Zero);
        var original = new PowerLimitWatchdog(options, "fixture-gpu");
        _ = original.Observe(Sample(t, 450, 450, 100), 0);
        var incident = original.Observe(Sample(t.AddSeconds(1), 450, 455, 100), 1);
        Check(incident.Status == PowerLimitWatchdogStatus.IncreasedLatched,
            "single-sample confirmation can latch when configured");

        var restored = PowerLimitWatchdog.Restore(original.SerializeState(), options, "fixture-gpu");
        var afterRestart = restored.Observe(Sample(t.AddSeconds(2), 450, 450, 100), 2);
        Check(afterRestart.Status == PowerLimitWatchdogStatus.IncreasedLatched &&
            afterRestart.IncidentLatched && restored.State.BaselineLimitWatts == 450,
            "serialized state preserves baseline and latched incident across restart");
        bool rejectedIdentity = false;
        try { _ = PowerLimitWatchdog.Restore(original.SerializeState(), options, "other-gpu"); }
        catch (ArgumentException) { rejectedIdentity = true; }
        Check(rejectedIdentity, "restoring state under another identity is rejected");
    }

    static void EnforcementTelemetryIsBoundedAndIndependent()
    {
        var options = new PowerLimitWatchdogOptions(
            MinimumConfirmationSamples: 2, ConfirmationSeconds: 0, BaselineSamples: 1,
            NotEnforcedToleranceWatts: 1);
        var t = new DateTimeOffset(2026, 1, 1, 4, 0, 0, TimeSpan.Zero);
        var supported = new PowerLimitWatchdog(options, "fixture-gpu");
        var baseline = supported.Observe(Sample(t, 450, 450, 200,
            enforced: 450, connector: 180, enforcementSupported: true), 0);
        Check(baseline.EnforcementTelemetrySupported && baseline.EnforcementStatus ==
            PowerLimitWatchdogStatus.Ok && baseline.DesiredConfiguredCapWatts == 450 &&
            baseline.ReportedManagementLimitWatts == 450 && baseline.EnforcedLimitWatts == 450 &&
            baseline.ConnectorPowerWatts == 180 && baseline.BoardPowerWatts == 200,
            "supported telemetry records desired, reported, enforced, connector, and board channels independently");
        var pending = supported.Observe(Sample(t.AddSeconds(1), 450, 450, 201,
            enforced: 452, connector: 181, enforcementSupported: true), 1);
        Check(pending.Status == PowerLimitWatchdogStatus.NotEnforcedPending &&
            pending.EnforcementStatus == PowerLimitWatchdogStatus.NotEnforcedPending &&
            pending.ConfirmationSamples == 1,
            "supported enforcement mismatch is bounded by confirmation samples");
        var confirmed = supported.Observe(Sample(t.AddSeconds(2), 450, 450, 202,
            enforced: 452, connector: 182, enforcementSupported: true), 2);
        Check(confirmed.Status == PowerLimitWatchdogStatus.NotEnforced &&
            confirmed.EnforcementStatus == PowerLimitWatchdogStatus.NotEnforced,
            "supported mismatch becomes NOT_ENFORCED only after bounded confirmation");

        var unsupported = new PowerLimitWatchdog(options, "fixture-gpu");
        var partial = unsupported.Observe(Sample(t, 450, 450, 200,
            enforced: null, connector: 170, enforcementSupported: false), 0);
        Check(partial.Status == PowerLimitWatchdogStatus.PartiallyVerified &&
            partial.EnforcementStatus == PowerLimitWatchdogStatus.PartiallyVerified &&
            !partial.EnforcementTelemetrySupported && partial.EnforcedLimitWatts is null &&
            partial.DesiredConfiguredCapWatts == 450 && partial.ReportedManagementLimitWatts == 450,
            "unsupported enforcement telemetry is explicitly PARTIALLY_VERIFIED");
    }

    static void ConfiguredCapChangesResetEnforcementConfirmation()
    {
        var options = new PowerLimitWatchdogOptions(
            MinimumConfirmationSamples: 2, ConfirmationSeconds: 0, BaselineSamples: 1);
        var t = new DateTimeOffset(2026, 1, 1, 5, 0, 0, TimeSpan.Zero);
        var watchdog = new PowerLimitWatchdog(options, "fixture-gpu");
        _ = watchdog.Observe(Sample(t, 450, 450, 100,
            enforced: 450, enforcementSupported: true), 0);
        _ = watchdog.Observe(Sample(t.AddSeconds(1), 450, 450, 100,
            enforced: 455, enforcementSupported: true), 1);
        var changed = watchdog.Observe(Sample(t.AddSeconds(2), 500, 500, 100,
            enforced: 500, enforcementSupported: true), 2);
        Check(changed.Status == PowerLimitWatchdogStatus.ConfigChanged &&
            changed.DesiredConfiguredCapWatts == 500 &&
            watchdog.State.PendingNotEnforcedSamples == 0,
            "a configured cap change is visible and resets pending enforcement evidence");
        var firstNewCapMismatch = watchdog.Observe(Sample(t.AddSeconds(3), 500, 500, 100,
            enforced: 505, enforcementSupported: true), 3);
        Check(firstNewCapMismatch.Status == PowerLimitWatchdogStatus.NotEnforcedPending &&
            firstNewCapMismatch.ConfirmationSamples == 1,
            "the new cap starts a fresh bounded confirmation window");
    }

    static void MissingAndResetEvidenceRemainVisible()
    {
        var options = new PowerLimitWatchdogOptions(BaselineSamples: 1);
        var t = new DateTimeOffset(2026, 1, 1, 6, 0, 0, TimeSpan.Zero);
        var missing = new PowerLimitWatchdog(options, "fixture-gpu");
        var missingResult = missing.Observe(Sample(t, 450, null, null,
            enforced: null, connector: null, enforcementSupported: false));
        Check(missingResult.Status == PowerLimitWatchdogStatus.Unavailable &&
            missingResult.DesiredConfiguredCapWatts == 450 &&
            missingResult.ReportedManagementLimitWatts is null &&
            missingResult.EnforcedLimitWatts is null && missingResult.ConnectorPowerWatts is null &&
            missingResult.BoardPowerWatts is null,
            "missing management, enforcement, connector, and board values remain null");

        var watchdog = new PowerLimitWatchdog(options, "fixture-gpu");
        _ = watchdog.Observe(Sample(t, 450, 450, 100,
            enforced: 450, connector: 160, enforcementSupported: true));
        Check(watchdog.State.LastDesiredConfiguredCapWatts == 450 &&
            watchdog.State.LastReportedManagementLimitWatts == 450 &&
            watchdog.State.LastEnforcedLimitWatts == 450 &&
            watchdog.State.LastConnectorPowerWatts == 160,
            "persisted state records each evidence channel before restart");
        var restored = PowerLimitWatchdog.Restore(watchdog.SerializeState(), options,
            "fixture-gpu");
        Check(restored.State.LastDesiredConfiguredCapWatts == 450 &&
            restored.State.LastEnforcedLimitWatts == 450 &&
            restored.State.LastConnectorPowerWatts == 160,
            "restart preserves independently recorded watchdog channels");
        restored.ArchiveBaseline();
        Check(restored.State.LastDesiredConfiguredCapWatts is null &&
            restored.State.LastEnforcedLimitWatts is null &&
            restored.State.LastConnectorPowerWatts is null,
            "baseline archive/reset makes prior evidence visibility explicit");
    }

    static void InvalidEvidenceCannotBridgeConfirmationOrPoisonIdentity()
    {
        var options = new PowerLimitWatchdogOptions(
            MinimumConfirmationSamples: 2, ConfirmationSeconds: 0, BaselineSamples: 1);
        var t = new DateTimeOffset(2026, 1, 1, 7, 0, 0, TimeSpan.Zero);
        var watchdog = new PowerLimitWatchdog(options, "fixture-gpu");
        _ = watchdog.Observe(Sample(t, 450, 450, 100), 0);
        var pending = watchdog.Observe(Sample(t.AddSeconds(1), 450, 455, 100), 1);
        Check(pending.ConfirmationSamples == 1, "increase begins confirmation");
        _ = watchdog.Observe(Sample(t.AddSeconds(2), 450, 455, 100,
            isFresh: false), 2);
        var afterStale = watchdog.Observe(Sample(t.AddSeconds(3), 450, 455, 100), 3);
        Check(afterStale.Status == PowerLimitWatchdogStatus.Increased &&
            afterStale.ConfirmationSamples == 1 && !afterStale.IncidentLatched,
            "stale evidence breaks rather than bridges confirmation");

        var savedDesired = watchdog.State.LastDesiredConfiguredCapWatts;
        var savedTimestamp = watchdog.State.LastHostTimestampUtc;
        var mismatch = watchdog.Observe(Sample(t.AddSeconds(4), 500, 500, 100,
            identity: "other-gpu"), 4);
        Check(mismatch.Status == PowerLimitWatchdogStatus.IdentityMismatch &&
            watchdog.State.LastDesiredConfiguredCapWatts == savedDesired &&
            watchdog.State.LastHostTimestampUtc == savedTimestamp,
            "identity mismatch cannot overwrite persisted observation channels or timestamps");
    }

    static PowerLimitObservation Sample(DateTimeOffset timestamp,
        double? configured, double? observed, double? board,
        bool isFresh = true, bool freshnessVerified = true, string identity = "fixture-gpu",
        double? enforced = null, double? connector = null,
        bool enforcementSupported = true) =>
        new(timestamp, configured, observed, board, isFresh,
            timestamp, freshnessVerified, "fixture-nvml", identity,
            DesiredConfiguredCapWatts: configured,
            EnforcedLimitWatts: enforced ?? observed,
            EnforcementTelemetrySupported: enforcementSupported,
            ConnectorPowerWatts: connector);

    static void Check(bool condition, string description)
    {
        if (!condition) throw new Exception("FAILED: " + description);
    }
}
