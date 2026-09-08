namespace ConnectorWatch;

/// <summary>
/// The two voltage thresholds used by the existing qualified-load detector.
/// They are comparison settings, not electrical safety limits.  Keeping them
/// here gives the pre-qualification guard and its callers one documented place
/// to refer to the production defaults without making the native decoder's
/// 6--16 V plausibility range an alarm threshold.
/// </summary>
public static class RailDetectionThresholdDefaults
{
    /// <summary>The sustained median/P05 comparison threshold.</summary>
    public const double ShiftVolts = .20;

    /// <summary>The single-observation sudden-droop threshold.</summary>
    public const double SuddenDroopVolts = .25;

    // These aliases make the names used by the legacy Config explicit while
    // keeping the stable values available to new domain callers.
    public const double SustainedShiftVolts = ShiftVolts;
    public const double RapidDiscontinuityVolts = SuddenDroopVolts;
}

/// <summary>One voltage observation presented to <see cref="CoarseRailGuard"/>.
/// ElapsedSeconds is an optional monotonic poll-clock value.  When it is
/// supplied for one observation it must be supplied for all observations in
/// the same continuity segment; this prevents wall-clock adjustments from
/// fabricating confirmation time.</summary>
public sealed record CoarseRailObservation(
    DateTimeOffset TimestampUtc,
    double? VoltageV,
    bool IsFresh = true,
    double? ElapsedSeconds = null)
{
    /// <summary>Builds an observation from the typed electrical contract.</summary>
    public static CoarseRailObservation FromElectricalSample(
        ElectricalSample sample, double? elapsedSeconds = null) =>
        new(sample.TimestampUtc, sample.Connector.VoltageV,
            sample.Freshness.IsFresh, elapsedSeconds);
}

/// <summary>Optional coarse gross-rail and discontinuity settings.</summary>
public sealed record CoarseRailGuardOptions
{
    /// <summary>
    /// Optional gross under-voltage alarm threshold.  Null disables the check;
    /// no 6 V/16 V safety threshold is invented by default.
    /// </summary>
    public double? GrossUnderVoltageV { get; init; }

    /// <summary>Optional gross over-voltage alarm threshold.</summary>
    public double? GrossOverVoltageV { get; init; }

    /// <summary>
    /// Absolute voltage change that starts a rapid discontinuity candidate.
    /// The production-compatible default is 0.25 V.
    /// </summary>
    public double RapidDiscontinuityVolts { get; init; } =
        RailDetectionThresholdDefaults.RapidDiscontinuityVolts;

    /// <summary>
    /// Time for an absolute or persistent discontinuity candidate to remain
    /// active before it is confirmed.  Zero preserves the legacy immediate
    /// sudden-droop behavior; callers can opt into elapsed-time confirmation.
    /// </summary>
    public TimeSpan ConfirmationDuration { get; init; } = TimeSpan.Zero;

    /// <summary>
    /// Minimum fresh observations in a candidate before confirmation.  Both
    /// this count and ConfirmationDuration must be satisfied.
    /// </summary>
    public int MinimumConfirmationSamples { get; init; } = 1;

    /// <summary>
    /// Maximum elapsed time between comparable observations.  A larger gap
    /// starts a new segment and cannot create a differential comparison.
    /// </summary>
    public double MaximumGapSeconds { get; init; } = 5;

    // Friendly aliases for integrations that use the shorter issue language.
    public double? UnderVoltageV
    {
        get => GrossUnderVoltageV;
        init => GrossUnderVoltageV = value;
    }

    public double? OverVoltageV
    {
        get => GrossOverVoltageV;
        init => GrossOverVoltageV = value;
    }

    public double RapidDropVolts
    {
        get => RapidDiscontinuityVolts;
        init => RapidDiscontinuityVolts = value;
    }

    public int ConfirmationSamples
    {
        get => MinimumConfirmationSamples;
        init => MinimumConfirmationSamples = value;
    }

    internal void Validate()
    {
        if (GrossUnderVoltageV is double under &&
            (!double.IsFinite(under) || under < 0))
            throw new ArgumentOutOfRangeException(nameof(GrossUnderVoltageV));
        if (GrossOverVoltageV is double over &&
            (!double.IsFinite(over) || over < 0))
            throw new ArgumentOutOfRangeException(nameof(GrossOverVoltageV));
        if (GrossUnderVoltageV.HasValue && GrossOverVoltageV.HasValue &&
            GrossUnderVoltageV > GrossOverVoltageV)
            throw new ArgumentException("Gross under-voltage must not exceed gross over-voltage.");
        if (!double.IsFinite(RapidDiscontinuityVolts) || RapidDiscontinuityVolts <= 0)
            throw new ArgumentOutOfRangeException(nameof(RapidDiscontinuityVolts));
        if (ConfirmationDuration < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(ConfirmationDuration));
        if (MinimumConfirmationSamples < 1)
            throw new ArgumentOutOfRangeException(nameof(MinimumConfirmationSamples));
        if (!double.IsFinite(MaximumGapSeconds) || MaximumGapSeconds <= 0)
            throw new ArgumentOutOfRangeException(nameof(MaximumGapSeconds));
    }
}

public enum CoarseRailConditionState
{
    Unavailable,
    None,
    Pending,
    Confirmed,
}

public enum CoarseRailAbsoluteKind
{
    None,
    GrossUnderVoltage,
    GrossOverVoltage,
}

public enum CoarseRailDiscontinuityKind
{
    None,
    RapidDrop,
    RapidRise,
}

/// <summary>Independent result of the absolute gross-rail check.</summary>
public readonly record struct CoarseRailAbsoluteOutcome(
    CoarseRailConditionState State,
    CoarseRailAbsoluteKind Kind,
    DateTimeOffset? SinceUtc,
    TimeSpan? Elapsed,
    double? VoltageV,
    string Detail)
{
    public bool IsActive => State is CoarseRailConditionState.Pending or
        CoarseRailConditionState.Confirmed;

    public bool IsConfirmed => State == CoarseRailConditionState.Confirmed;

    public static CoarseRailAbsoluteOutcome Unavailable(string detail) =>
        new(CoarseRailConditionState.Unavailable, CoarseRailAbsoluteKind.None,
            null, null, null, detail);

    public static CoarseRailAbsoluteOutcome None(double voltageV) =>
        new(CoarseRailConditionState.None, CoarseRailAbsoluteKind.None,
            null, TimeSpan.Zero, voltageV, "No gross absolute threshold crossed.");
}

/// <summary>Independent result of the differential discontinuity check.</summary>
public readonly record struct CoarseRailDiscontinuityOutcome(
    CoarseRailConditionState State,
    CoarseRailDiscontinuityKind Kind,
    DateTimeOffset? SinceUtc,
    TimeSpan? Elapsed,
    double? DeltaVolts,
    string Detail)
{
    public bool IsActive => State is CoarseRailConditionState.Pending or
        CoarseRailConditionState.Confirmed;

    public bool IsConfirmed => State == CoarseRailConditionState.Confirmed;

    public static CoarseRailDiscontinuityOutcome Unavailable(string detail) =>
        new(CoarseRailConditionState.Unavailable, CoarseRailDiscontinuityKind.None,
            null, null, null, detail);

    public static CoarseRailDiscontinuityOutcome None() =>
        new(CoarseRailConditionState.None, CoarseRailDiscontinuityKind.None,
            null, TimeSpan.Zero, null, "No rapid discontinuity detected.");
}

/// <summary>Combined guard result; Absolute and Discontinuity remain separate
/// so one outcome never masks the other.</summary>
public sealed record CoarseRailGuardResult(
    DateTimeOffset? TimestampUtc,
    bool IsValid,
    bool IsFresh,
    bool IsGap,
    double? VoltageV,
    CoarseRailAbsoluteOutcome Absolute,
    CoarseRailDiscontinuityOutcome Discontinuity,
    string Detail)
{
    /// <summary>True when either independent outcome is pending or confirmed.</summary>
    public bool BlocksQualification => !IsValid ||
        Absolute.IsActive || Discontinuity.IsActive;

    public bool CanQualify => !BlocksQualification;

    public bool HasAlarm => Absolute.IsConfirmed || Discontinuity.IsConfirmed;
}

/// <summary>
/// A pre-qualification rail guard.  It validates fresh finite voltage before
/// any load/reference comparison, evaluates optional absolute thresholds, and
/// independently tracks a persistent rapid drop/rise.  A gap, stale value,
/// invalid value, or clock discontinuity resets the differential baseline;
/// the first valid value in the next segment is never compared with the last
/// value in the previous segment.
/// </summary>
public sealed class CoarseRailGuard
{
    readonly CoarseRailGuardOptions options;
    CoarseRailAbsoluteKind absoluteKind;
    DateTimeOffset? absoluteSinceUtc;
    double? absoluteSinceClock;
    int absoluteSamples;
    CoarseRailDiscontinuityKind discontinuityKind;
    double discontinuityBaseline;
    DateTimeOffset? discontinuitySinceUtc;
    double? discontinuitySinceClock;
    int discontinuitySamples;
    double? previousVoltage;
    DateTimeOffset? previousTimestampUtc;
    double? previousElapsedSeconds;
    bool awaitingPostGap;

    public CoarseRailGuard(CoarseRailGuardOptions? options = null)
    {
        this.options = options ?? new CoarseRailGuardOptions();
        this.options.Validate();
    }

    public CoarseRailGuardOptions Options => options;

    /// <summary>Forget all continuity and confirmation state.</summary>
    public void Reset() => ResetState(awaitingPostGap: false);

    /// <summary>
    /// Marks a source gap.  The next valid observation is accepted as a new
    /// absolute first observation but cannot produce a differential result.
    /// </summary>
    public void Gap() => ResetState(awaitingPostGap: true);

    public CoarseRailGuardResult Add(CoarseRailObservation observation) =>
        Observe(observation);

    public CoarseRailGuardResult Observe(CoarseRailObservation observation)
    {
        if (!observation.IsFresh || !observation.VoltageV.HasValue ||
            !double.IsFinite(observation.VoltageV.Value) || observation.TimestampUtc == default)
        {
            ResetState(awaitingPostGap: true);
            string reason = !observation.IsFresh ? "Voltage observation is stale."
                : !observation.VoltageV.HasValue ? "Voltage observation is missing."
                : !double.IsFinite(observation.VoltageV.Value) ? "Voltage observation is non-finite."
                : "Voltage observation has no usable timestamp.";
            return InvalidResult(observation, reason);
        }

        if (observation.ElapsedSeconds is double elapsed &&
            (!double.IsFinite(elapsed) || elapsed < 0))
        {
            ResetState(awaitingPostGap: true);
            return InvalidResult(observation, "Monotonic elapsed time is invalid.");
        }

        bool gap = awaitingPostGap;
        if (HasPreviousClock() && !IsComparableClock(observation, out var gapReason))
        {
            ResetState(awaitingPostGap: false);
            gap = true;
            gapReason ??= "Observation continuity was interrupted.";
            // Keep the current observation as the first sample of its new
            // segment, while reporting the interruption to the caller.
            var first = AcceptFirst(observation, gap);
            return first with { Detail = gapReason + " " + first.Detail };
        }

        double voltage = observation.VoltageV.Value;
        double clock = ClockValue(observation);
        var absolute = EvaluateAbsolute(voltage, observation.TimestampUtc, clock);
        var differential = previousVoltage.HasValue && !gap
            ? EvaluateDiscontinuity(voltage, previousVoltage.Value,
                observation.TimestampUtc, clock)
            : CoarseRailDiscontinuityOutcome.None();

        previousVoltage = voltage;
        previousTimestampUtc = observation.TimestampUtc;
        previousElapsedSeconds = observation.ElapsedSeconds;
        awaitingPostGap = false;
        return BuildResult(observation, voltage, absolute, differential, gap);
    }

    /// <summary>Convenience overload for callers that already have scalar V.</summary>
    public CoarseRailGuardResult Observe(DateTimeOffset timestampUtc, double? voltageV,
        bool isFresh = true, double? elapsedSeconds = null) =>
        Observe(new CoarseRailObservation(timestampUtc, voltageV, isFresh, elapsedSeconds));

    /// <summary>Convenience overload for the typed electrical sample contract.</summary>
    public CoarseRailGuardResult Observe(ElectricalSample sample,
        double? elapsedSeconds = null) =>
        Observe(CoarseRailObservation.FromElectricalSample(sample, elapsedSeconds));

    CoarseRailGuardResult AcceptFirst(CoarseRailObservation observation, bool gap)
    {
        double voltage = observation.VoltageV!.Value;
        double clock = ClockValue(observation);
        var absolute = EvaluateAbsolute(voltage, observation.TimestampUtc, clock);
        previousVoltage = voltage;
        previousTimestampUtc = observation.TimestampUtc;
        previousElapsedSeconds = observation.ElapsedSeconds;
        awaitingPostGap = false;
        return BuildResult(observation, voltage, absolute,
            CoarseRailDiscontinuityOutcome.None(), gap);
    }

    CoarseRailGuardResult InvalidResult(CoarseRailObservation observation, string detail) =>
        new(observation.TimestampUtc == default ? null : observation.TimestampUtc,
            false, observation.IsFresh, true, observation.VoltageV,
            CoarseRailAbsoluteOutcome.Unavailable(detail),
            CoarseRailDiscontinuityOutcome.Unavailable(detail), detail);

    CoarseRailGuardResult BuildResult(CoarseRailObservation observation, double voltage,
        CoarseRailAbsoluteOutcome absolute, CoarseRailDiscontinuityOutcome differential,
        bool gap)
    {
        string detail = gap ? "A new continuity segment began. " : "";
        if (absolute.IsActive)
            detail += absolute.Detail + " ";
        if (differential.IsActive)
            detail += differential.Detail;
        if (detail.Length == 0)
            detail = "Fresh voltage is eligible for load qualification.";
        return new(observation.TimestampUtc, true, true, gap, voltage,
            absolute, differential, detail.Trim());
    }

    CoarseRailAbsoluteOutcome EvaluateAbsolute(double voltage,
        DateTimeOffset timestampUtc, double clock)
    {
        CoarseRailAbsoluteKind kind = GrossKind(voltage);
        if (kind == CoarseRailAbsoluteKind.None)
        {
            absoluteKind = kind;
            absoluteSinceUtc = null;
            absoluteSinceClock = null;
            absoluteSamples = 0;
            return CoarseRailAbsoluteOutcome.None(voltage);
        }

        if (absoluteKind != kind)
        {
            absoluteKind = kind;
            absoluteSinceUtc = timestampUtc;
            absoluteSinceClock = clock;
            absoluteSamples = 1;
        }
        else
        {
            absoluteSamples++;
        }

        TimeSpan elapsed = ElapsedSince(absoluteSinceUtc, absoluteSinceClock,
            timestampUtc, clock);
        var state = Confirmed(elapsed, absoluteSamples)
            ? CoarseRailConditionState.Confirmed
            : CoarseRailConditionState.Pending;
        string direction = kind == CoarseRailAbsoluteKind.GrossUnderVoltage
            ? "under-voltage" : "over-voltage";
        return new(state, kind, absoluteSinceUtc, elapsed, voltage,
            $"Gross {direction} candidate ({voltage:F3} V) is {state.ToString().ToLowerInvariant()}.");
    }

    CoarseRailDiscontinuityOutcome EvaluateDiscontinuity(double voltage,
        double priorVoltage, DateTimeOffset timestampUtc, double clock)
    {
        if (discontinuityKind != CoarseRailDiscontinuityKind.None)
        {
            bool persists = discontinuityKind == CoarseRailDiscontinuityKind.RapidDrop
                ? voltage <= discontinuityBaseline - options.RapidDiscontinuityVolts
                : voltage >= discontinuityBaseline + options.RapidDiscontinuityVolts;
            if (!persists)
            {
                discontinuityKind = CoarseRailDiscontinuityKind.None;
                discontinuitySinceUtc = null;
                discontinuitySinceClock = null;
                discontinuitySamples = 0;
            }
        }

        if (discontinuityKind == CoarseRailDiscontinuityKind.None)
        {
            double delta = voltage - priorVoltage;
            if (delta <= -options.RapidDiscontinuityVolts)
            {
                discontinuityKind = CoarseRailDiscontinuityKind.RapidDrop;
                discontinuityBaseline = priorVoltage;
                discontinuitySinceUtc = timestampUtc;
                discontinuitySinceClock = clock;
                discontinuitySamples = 1;
            }
            else if (delta >= options.RapidDiscontinuityVolts)
            {
                discontinuityKind = CoarseRailDiscontinuityKind.RapidRise;
                discontinuityBaseline = priorVoltage;
                discontinuitySinceUtc = timestampUtc;
                discontinuitySinceClock = clock;
                discontinuitySamples = 1;
            }
        }
        else
        {
            discontinuitySamples++;
        }

        if (discontinuityKind == CoarseRailDiscontinuityKind.None)
            return CoarseRailDiscontinuityOutcome.None();

        TimeSpan elapsed = ElapsedSince(discontinuitySinceUtc, discontinuitySinceClock,
            timestampUtc, clock);
        var state = Confirmed(elapsed, discontinuitySamples)
            ? CoarseRailConditionState.Confirmed
            : CoarseRailConditionState.Pending;
        double signedDelta = voltage - discontinuityBaseline;
        string direction = discontinuityKind == CoarseRailDiscontinuityKind.RapidDrop
            ? "drop" : "rise";
        return new(state, discontinuityKind, discontinuitySinceUtc, elapsed,
            signedDelta, $"Rapid {direction} candidate ({signedDelta:+0.000;-0.000;0} V) is {state.ToString().ToLowerInvariant()}.");
    }

    CoarseRailAbsoluteKind GrossKind(double voltage)
    {
        if (options.GrossUnderVoltageV is double under && voltage < under)
            return CoarseRailAbsoluteKind.GrossUnderVoltage;
        if (options.GrossOverVoltageV is double over && voltage > over)
            return CoarseRailAbsoluteKind.GrossOverVoltage;
        return CoarseRailAbsoluteKind.None;
    }

    bool Confirmed(TimeSpan elapsed, int samples) =>
        elapsed >= options.ConfirmationDuration &&
        samples >= options.MinimumConfirmationSamples;

    bool HasPreviousClock() => previousTimestampUtc.HasValue || previousElapsedSeconds.HasValue;

    bool IsComparableClock(CoarseRailObservation observation, out string? reason)
    {
        reason = null;
        if (observation.ElapsedSeconds.HasValue != previousElapsedSeconds.HasValue)
        {
            reason = "Observation clock source changed.";
            return false;
        }

        double delta = observation.ElapsedSeconds.HasValue
            ? observation.ElapsedSeconds.Value - previousElapsedSeconds!.Value
            : (observation.TimestampUtc - previousTimestampUtc!.Value).TotalSeconds;
        if (!double.IsFinite(delta) || delta <= 0)
        {
            reason = "Observation timestamp is repeated or moved backwards.";
            return false;
        }
        if (delta > options.MaximumGapSeconds)
        {
            reason = $"Observation gap ({delta:F3} s) exceeded {options.MaximumGapSeconds:F3} s.";
            return false;
        }
        return true;
    }

    static double ClockValue(CoarseRailObservation observation) =>
        observation.ElapsedSeconds ?? observation.TimestampUtc.UtcDateTime.Ticks /
        (double)TimeSpan.TicksPerSecond;

    static TimeSpan ElapsedSince(DateTimeOffset? sinceUtc, double? sinceClock,
        DateTimeOffset nowUtc, double nowClock) => sinceClock.HasValue
        ? TimeSpan.FromSeconds(Math.Max(0, nowClock - sinceClock.Value))
        : nowUtc - sinceUtc!.Value;

    void ResetState(bool awaitingPostGap)
    {
        absoluteKind = CoarseRailAbsoluteKind.None;
        absoluteSinceUtc = null;
        absoluteSinceClock = null;
        absoluteSamples = 0;
        discontinuityKind = CoarseRailDiscontinuityKind.None;
        discontinuityBaseline = 0;
        discontinuitySinceUtc = null;
        discontinuitySinceClock = null;
        discontinuitySamples = 0;
        previousVoltage = null;
        previousTimestampUtc = null;
        previousElapsedSeconds = null;
        this.awaitingPostGap = awaitingPostGap;
    }
}
