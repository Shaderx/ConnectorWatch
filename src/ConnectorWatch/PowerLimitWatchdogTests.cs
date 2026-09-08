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

    static PowerLimitObservation Sample(DateTimeOffset timestamp,
        double? configured, double? observed, double? board,
        bool isFresh = true, bool freshnessVerified = true, string identity = "fixture-gpu") =>
        new(timestamp, configured, observed, board, isFresh,
            timestamp, freshnessVerified, "fixture-nvml", identity);

    static void Check(bool condition, string description)
    {
        if (!condition) throw new Exception("FAILED: " + description);
    }
}
