namespace ConnectorWatch;

/// <summary>Offline behavior checks for the pre-qualification rail guard.</summary>
public static class CoarseRailGuardTests
{
    public static void Run()
    {
        var start = new DateTimeOffset(2026, 9, 9, 0, 0, 0, TimeSpan.Zero);

        var defaults = new CoarseRailGuardOptions();
        Check(Math.Abs(defaults.RapidDiscontinuityVolts - .25) < .000001,
            "rapid discontinuity default remains 0.25 V");
        Check(Math.Abs(RailDetectionThresholdDefaults.ShiftVolts - .20) < .000001,
            "qualified shift default remains 0.20 V");

        // The decoder's plausibility endpoints are not safety alarms.  With
        // no configured gross thresholds they remain eligible values.
        var plausibility = new CoarseRailGuard();
        var lowPlausible = plausibility.Observe(start, 6, elapsedSeconds: 0);
        Check(lowPlausible.IsValid && !lowPlausible.Absolute.IsActive,
            "6 V plausibility endpoint is not an alarm by default");
        var highPlausible = plausibility.Observe(start.AddSeconds(1), 16,
            elapsedSeconds: 1);
        Check(highPlausible.IsValid && !highPlausible.Absolute.IsActive,
            "16 V plausibility endpoint is not an alarm by default");

        var timed = new CoarseRailGuard(new CoarseRailGuardOptions
        {
            ConfirmationDuration = TimeSpan.FromSeconds(2),
            MinimumConfirmationSamples = 2,
        });
        var first = timed.Observe(start, 12.0, elapsedSeconds: 0);
        Check(first.Discontinuity.Kind == CoarseRailDiscontinuityKind.None &&
            first.CanQualify, "first observation establishes no differential alarm");
        var candidate = timed.Observe(start.AddSeconds(1), 11.70,
            elapsedSeconds: 1);
        Check(candidate.Discontinuity.Kind == CoarseRailDiscontinuityKind.RapidDrop &&
            candidate.Discontinuity.State == CoarseRailConditionState.Pending &&
            !candidate.Discontinuity.IsConfirmed,
            "rapid drop waits for elapsed confirmation");
        var stillCandidate = timed.Observe(start.AddSeconds(2), 11.65,
            elapsedSeconds: 2);
        Check(stillCandidate.Discontinuity.State == CoarseRailConditionState.Pending,
            "rapid drop does not confirm before elapsed duration");
        var confirmed = timed.Observe(start.AddSeconds(3), 11.60,
            elapsedSeconds: 3);
        Check(confirmed.Discontinuity.IsConfirmed && confirmed.HasAlarm,
            "rapid drop confirms after elapsed duration and sample count");

        // Absolute and differential outcomes are evaluated independently on
        // the same sample; neither masks the other.
        var independent = new CoarseRailGuard(new CoarseRailGuardOptions
        {
            GrossUnderVoltageV = 11.8,
            GrossOverVoltageV = 12.3,
            ConfirmationDuration = TimeSpan.Zero,
        });
        _ = independent.Observe(start, 12.0, elapsedSeconds: 0);
        var both = independent.Observe(start.AddSeconds(1), 11.70,
            elapsedSeconds: 1);
        Check(both.Absolute.Kind == CoarseRailAbsoluteKind.GrossUnderVoltage &&
            both.Absolute.IsConfirmed,
            "gross under-voltage is independently confirmed");
        Check(both.Discontinuity.Kind == CoarseRailDiscontinuityKind.RapidDrop &&
            both.Discontinuity.IsConfirmed,
            "rapid drop remains independently confirmed");

        // Stale/invalid values reset state and cannot fabricate a comparison
        // from the last valid voltage.  The next valid value is post-gap.
        var gaps = new CoarseRailGuard();
        _ = gaps.Observe(start, 12.0, elapsedSeconds: 0);
        var stale = gaps.Observe(new CoarseRailObservation(start.AddSeconds(1),
            11.0, IsFresh: false, ElapsedSeconds: 1));
        Check(!stale.IsValid && stale.IsGap &&
            stale.Discontinuity.State == CoarseRailConditionState.Unavailable,
            "stale voltage is unavailable and resets differential state");
        var afterStale = gaps.Observe(start.AddSeconds(2), 11.0,
            elapsedSeconds: 2);
        Check(afterStale.IsGap && afterStale.Discontinuity.Kind == CoarseRailDiscontinuityKind.None,
            "first post-stale voltage is not compared with pre-gap voltage");

        var invalid = gaps.Observe(start.AddSeconds(3), double.NaN,
            elapsedSeconds: 3);
        Check(!invalid.IsValid && invalid.IsGap,
            "non-finite voltage is rejected");
        var afterInvalid = gaps.Observe(start.AddSeconds(4), 12.0,
            elapsedSeconds: 4);
        Check(afterInvalid.IsGap && afterInvalid.Discontinuity.Kind == CoarseRailDiscontinuityKind.None,
            "first post-invalid voltage starts a fresh baseline");

        var clockGap = new CoarseRailGuard(new CoarseRailGuardOptions
        {
            MaximumGapSeconds = 2,
        });
        _ = clockGap.Observe(start, 12.0, elapsedSeconds: 0);
        var gap = clockGap.Observe(start.AddSeconds(3), 11.0,
            elapsedSeconds: 3);
        Check(gap.IsGap && gap.Discontinuity.Kind == CoarseRailDiscontinuityKind.None,
            "over-age voltage gap does not create a discontinuity");

        Console.WriteLine("PASS: coarse rail guard, independent outcomes and timed confirmation.");
    }

    static void Check(bool condition, string name)
    {
        if (!condition) throw new Exception("FAILED: " + name);
    }
}

