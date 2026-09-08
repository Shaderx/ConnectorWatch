namespace ConnectorWatch;

/// <summary>A load observation supplied to the legacy load qualifier.</summary>
public sealed record LoadQualificationObservation(
    DateTimeOffset TimestampUtc,
    double? LoadWatts,
    bool IsFresh = true,
    double? ElapsedSeconds = null)
{
    /// <summary>
    /// Creates an observation from the selected typed electrical source.  The
    /// selection is exact: a missing selected field is unavailable rather than
    /// silently replaced by another power source.
    /// </summary>
    public static LoadQualificationObservation FromElectricalSample(
        ElectricalSample sample, AnalysisLoadSource source,
        double? elapsedSeconds = null) 
    {
        var selected = sample.SelectAnalysisLoad(source);
        double? watts = selected.IsAvailable
            ? selected.ToAnalysisPowerWatts(sample)
            : null;
        return new(sample.TimestampUtc, watts,
            sample.Freshness.IsFresh && watts.HasValue, elapsedSeconds);
    }
}

/// <summary>
/// Settings for <see cref="HystereticLoadQualifier"/>.  BinWatts retains the
/// legacy 25 W grouping.  BoundaryHysteresisWatts is deliberately small: it
/// only suppresses chatter around a bin boundary (for example 449/451 W),
/// while a materially different load starts a new qualification segment.
/// </summary>
public sealed record LoadQualificationOptions
{
    public double MinimumLoadWatts { get; init; } = 100;
    public double MaximumLoadWatts { get; init; } = 1000;
    public double BinWatts { get; init; } = 25;
    public double BoundaryHysteresisWatts { get; init; } = 2.5;

    /// <summary>
    /// Maximum raw sample-to-sample change that can remain comparable while a
    /// hysteresis band is held.  Null means one bin width.
    /// </summary>
    public double? MaximumComparableDeltaWatts { get; init; }

    public int MinimumStableSamples { get; init; } = 5;
    public TimeSpan MinimumStableDuration { get; init; } = TimeSpan.Zero;
    public double MaximumGapSeconds { get; init; } = 5;

    // Aliases make mapping from the existing Config and issue terminology
    // explicit without requiring a caller to know the longer domain names.
    public double MinLoadWatts
    {
        get => MinimumLoadWatts;
        init => MinimumLoadWatts = value;
    }

    public double MaxLoadWatts
    {
        get => MaximumLoadWatts;
        init => MaximumLoadWatts = value;
    }

    public double HysteresisWatts
    {
        get => BoundaryHysteresisWatts;
        init => BoundaryHysteresisWatts = value;
    }

    public int StableSamples
    {
        get => MinimumStableSamples;
        init => MinimumStableSamples = value;
    }

    public TimeSpan StableDuration
    {
        get => MinimumStableDuration;
        init => MinimumStableDuration = value;
    }

    internal void Validate()
    {
        if (!double.IsFinite(MinimumLoadWatts) || MinimumLoadWatts < 0)
            throw new ArgumentOutOfRangeException(nameof(MinimumLoadWatts));
        if (!double.IsFinite(MaximumLoadWatts) || MaximumLoadWatts < MinimumLoadWatts)
            throw new ArgumentOutOfRangeException(nameof(MaximumLoadWatts));
        if (!double.IsFinite(BinWatts) || BinWatts <= 0 || BinWatts > int.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(BinWatts));
        if (!double.IsFinite(BoundaryHysteresisWatts) ||
            BoundaryHysteresisWatts < 0 || BoundaryHysteresisWatts >= BinWatts)
            throw new ArgumentOutOfRangeException(nameof(BoundaryHysteresisWatts));
        if (MaximumComparableDeltaWatts is double comparable &&
            (!double.IsFinite(comparable) || comparable <= 0))
            throw new ArgumentOutOfRangeException(nameof(MaximumComparableDeltaWatts));
        if (MinimumStableSamples < 1)
            throw new ArgumentOutOfRangeException(nameof(MinimumStableSamples));
        if (MinimumStableDuration < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(MinimumStableDuration));
        if (!double.IsFinite(MaximumGapSeconds) || MaximumGapSeconds <= 0)
            throw new ArgumentOutOfRangeException(nameof(MaximumGapSeconds));
        if (MaximumLoadWatts > int.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(MaximumLoadWatts));
    }
}

public enum LoadQualificationState
{
    NoObservation,
    Invalid,
    Gap,
    Settling,
    Qualified,
}

public enum LoadQualificationTransition
{
    None,
    FirstObservation,
    HysteresisHold,
    BandChanged,
}

/// <summary>Outcome of one load qualification update.</summary>
public sealed record LoadQualificationResult(
    LoadQualificationState State,
    LoadQualificationTransition Transition,
    DateTimeOffset? TimestampUtc,
    int? Bin,
    int? RawBin,
    double? LoadWatts,
    double? CanonicalLoadWatts,
    int StableSamples,
    int StableTarget,
    TimeSpan StableDuration,
    bool IsComparable,
    bool IsGap,
    string Detail)
{
    public bool IsQualified => State == LoadQualificationState.Qualified;

    /// <summary>The legacy wire-style status suitable for an analysis row.</summary>
    public string Status => State switch
    {
        LoadQualificationState.NoObservation => "NO_OBSERVATION",
        LoadQualificationState.Invalid => "INVALID_LOAD",
        LoadQualificationState.Gap => "LOAD_GAP",
        LoadQualificationState.Settling => "LOAD_SETTLING",
        LoadQualificationState.Qualified => "LOAD_QUALIFIED",
        _ => "INVALID_LOAD",
    };

    /// <summary>
    /// A stable representative that floors into BinWatts' current band.  A
    /// caller feeding legacy Analysis should use this value rather than the
    /// raw 449/451 W observation, so the old exact-bin code sees one band.
    /// </summary>
    public double? AnalysisLoadWatts => CanonicalLoadWatts;

    public double? ComparableLoadWatts => IsComparable ? CanonicalLoadWatts : null;
}

/// <summary>
/// Continuous Schmitt-trigger-style qualifier for the legacy watt axis.
/// The current band is held until a load crosses its boundary plus/minus a
/// small hysteresis margin.  Consequently 449/451 W does not alternate
/// between 425 W and 450 W bins, while a larger change starts a fresh settling
/// interval.  Missing, stale, invalid, repeated, and over-age observations
/// reset continuity and cannot be compared across the gap.
/// </summary>
public class HystereticLoadQualifier
{
    readonly LoadQualificationOptions options;
    readonly double maximumComparableDeltaWatts;
    int? currentBand;
    double? previousLoadWatts;
    DateTimeOffset? previousTimestampUtc;
    double? previousElapsedSeconds;
    DateTimeOffset? stableSinceUtc;
    double? stableSinceElapsedSeconds;
    int stableSamples;
    bool awaitingPostGap;

    public HystereticLoadQualifier(LoadQualificationOptions? options = null)
    {
        this.options = options ?? new LoadQualificationOptions();
        this.options.Validate();
        maximumComparableDeltaWatts =
            this.options.MaximumComparableDeltaWatts ?? this.options.BinWatts;
    }

    public LoadQualificationOptions Options => options;
    public bool IsQualified => currentBand.HasValue &&
        stableSamples >= options.MinimumStableSamples &&
        CurrentStableDuration >= options.MinimumStableDuration;
    public int? CurrentBand => currentBand;
    public int StableSamples => stableSamples;

    /// <summary>Forget all observations and qualification state.</summary>
    public void Reset() => ResetState(awaitingPostGap: false);

    /// <summary>
    /// Marks a source gap.  The next valid sample starts a new segment and is
    /// returned as Gap so a caller cannot accidentally compare it with older
    /// data.
    /// </summary>
    public void Gap() => ResetState(awaitingPostGap: true);

    public LoadQualificationResult Add(LoadQualificationObservation observation) =>
        Observe(observation);

    public LoadQualificationResult Observe(LoadQualificationObservation observation)
    {
        if (!observation.IsFresh || !observation.LoadWatts.HasValue ||
            !double.IsFinite(observation.LoadWatts.Value) || observation.TimestampUtc == default)
        {
            ResetState(awaitingPostGap: true);
            string reason = !observation.IsFresh ? "Load observation is stale or unavailable."
                : !observation.LoadWatts.HasValue ? "Load observation is missing."
                : !double.IsFinite(observation.LoadWatts.Value) ? "Load observation is non-finite."
                : "Load observation has no usable timestamp.";
            return InvalidResult(observation, reason);
        }

        double load = observation.LoadWatts.Value;
        if (observation.ElapsedSeconds is double elapsed &&
            (!double.IsFinite(elapsed) || elapsed < 0))
        {
            ResetState(awaitingPostGap: true);
            return InvalidResult(observation, "Monotonic elapsed time is invalid.");
        }
        if (load < options.MinimumLoadWatts || load > options.MaximumLoadWatts)
        {
            ResetState(awaitingPostGap: true);
            return InvalidResult(observation,
                $"Load {load:F3} W is outside the analysis range " +
                $"[{options.MinimumLoadWatts:F3}, {options.MaximumLoadWatts:F3}] W.");
        }

        bool gap = awaitingPostGap;
        if (HasPreviousClock() && !IsComparableClock(observation, out var gapReason))
        {
            ResetState(awaitingPostGap: false);
            var firstAfterGap = AcceptFirst(observation, gap: true);
            return firstAfterGap with
            {
                Detail = gapReason + " " + firstAfterGap.Detail,
                IsGap = true,
                State = LoadQualificationState.Gap,
                IsComparable = false,
            };
        }

        int rawBin = BinFor(load);
        LoadQualificationTransition transition;
        bool comparable;
        if (!currentBand.HasValue)
        {
            currentBand = rawBin;
            StartStable(observation);
            transition = LoadQualificationTransition.FirstObservation;
            comparable = false;
        }
        else if (previousLoadWatts.HasValue &&
            Math.Abs(load - previousLoadWatts.Value) > maximumComparableDeltaWatts)
        {
            currentBand = rawBin;
            StartStable(observation);
            transition = LoadQualificationTransition.BandChanged;
            comparable = false;
        }
        else if (rawBin != currentBand.Value && HoldsCurrentBand(load, rawBin))
        {
            stableSamples++;
            transition = LoadQualificationTransition.HysteresisHold;
            comparable = true;
        }
        else if (rawBin != currentBand.Value)
        {
            currentBand = rawBin;
            StartStable(observation);
            transition = LoadQualificationTransition.BandChanged;
            comparable = false;
        }
        else
        {
            stableSamples++;
            transition = LoadQualificationTransition.None;
            comparable = true;
        }

        TimeSpan stableDuration = CurrentStableDurationFor(observation);
        bool qualified = !gap && stableSamples >= options.MinimumStableSamples &&
            stableDuration >= options.MinimumStableDuration;
        var state = gap ? LoadQualificationState.Gap
            : qualified ? LoadQualificationState.Qualified
            : LoadQualificationState.Settling;
        double canonical = CanonicalFor(currentBand.Value);
        previousLoadWatts = load;
        previousTimestampUtc = observation.TimestampUtc;
        previousElapsedSeconds = observation.ElapsedSeconds;
        awaitingPostGap = false;
        string detail = transition switch
        {
            LoadQualificationTransition.FirstObservation =>
                "First valid load observation started a qualification segment.",
            LoadQualificationTransition.HysteresisHold =>
                $"Load {load:F3} W remains within the {currentBand.Value} W hysteresis band.",
            LoadQualificationTransition.BandChanged =>
                $"Load moved to the {currentBand.Value} W band; qualification restarted.",
            _ => qualified ? "Load band remains qualified." : "Load band remains settling.",
        };
        return new(state, transition, observation.TimestampUtc, currentBand, rawBin,
            load, canonical, stableSamples, options.MinimumStableSamples,
            stableDuration, comparable && !gap, gap, detail);
    }

    public LoadQualificationResult Observe(DateTimeOffset timestampUtc, double? loadWatts,
        bool isFresh = true, double? elapsedSeconds = null) =>
        Observe(new LoadQualificationObservation(timestampUtc, loadWatts,
            isFresh, elapsedSeconds));

    /// <summary>Convenience overload for typed electrical samples.</summary>
    public LoadQualificationResult Observe(ElectricalSample sample,
        AnalysisLoadSource source, double? elapsedSeconds = null) =>
        Observe(LoadQualificationObservation.FromElectricalSample(sample, source,
            elapsedSeconds));

    LoadQualificationResult AcceptFirst(LoadQualificationObservation observation, bool gap)
    {
        double load = observation.LoadWatts!.Value;
        currentBand = BinFor(load);
        StartStable(observation);
        double canonical = CanonicalFor(currentBand.Value);
        previousLoadWatts = load;
        previousTimestampUtc = observation.TimestampUtc;
        previousElapsedSeconds = observation.ElapsedSeconds;
        awaitingPostGap = false;
        return new(LoadQualificationState.Gap, LoadQualificationTransition.FirstObservation,
            observation.TimestampUtc, currentBand, currentBand, load, canonical,
            stableSamples, options.MinimumStableSamples, TimeSpan.Zero,
            false, gap, "First valid load observation after a continuity gap.");
    }

    LoadQualificationResult InvalidResult(LoadQualificationObservation observation,
        string detail) =>
        new(LoadQualificationState.Invalid, LoadQualificationTransition.None,
            observation.TimestampUtc == default ? null : observation.TimestampUtc,
            null, null, observation.LoadWatts, null, 0,
            options.MinimumStableSamples, TimeSpan.Zero, false, true, detail);

    bool HoldsCurrentBand(double load, int rawBin)
    {
        if (!currentBand.HasValue || rawBin == currentBand.Value)
            return false;
        double boundary = rawBin > currentBand.Value
            ? currentBand.Value + options.BinWatts
            : currentBand.Value;
        return rawBin > currentBand.Value
            ? load < boundary + options.BoundaryHysteresisWatts
            : load > boundary - options.BoundaryHysteresisWatts;
    }

    int BinFor(double load)
    {
        double bin = Math.Floor(load / options.BinWatts) * options.BinWatts;
        return checked((int)bin);
    }

    double CanonicalFor(int band) => band + options.BinWatts * .5;

    void StartStable(LoadQualificationObservation observation)
    {
        stableSamples = 1;
        stableSinceUtc = observation.TimestampUtc;
        stableSinceElapsedSeconds = observation.ElapsedSeconds;
    }

    TimeSpan CurrentStableDuration => stableSinceUtc.HasValue
        ? previousElapsedSeconds.HasValue && stableSinceElapsedSeconds.HasValue
            ? TimeSpan.FromSeconds(Math.Max(0,
                previousElapsedSeconds.Value - stableSinceElapsedSeconds.Value))
            : previousTimestampUtc!.Value - stableSinceUtc.Value
        : TimeSpan.Zero;

    TimeSpan CurrentStableDurationFor(LoadQualificationObservation observation) =>
        stableSinceElapsedSeconds.HasValue && observation.ElapsedSeconds.HasValue
            ? TimeSpan.FromSeconds(Math.Max(0,
                observation.ElapsedSeconds.Value - stableSinceElapsedSeconds.Value))
            : observation.TimestampUtc - stableSinceUtc!.Value;

    bool HasPreviousClock() => previousTimestampUtc.HasValue || previousElapsedSeconds.HasValue;

    bool IsComparableClock(LoadQualificationObservation observation, out string? reason)
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

    static double ClockValue(LoadQualificationObservation observation) =>
        observation.ElapsedSeconds ?? observation.TimestampUtc.UtcDateTime.Ticks /
        (double)TimeSpan.TicksPerSecond;

    LoadQualificationResult BuildUnused() =>
        new(LoadQualificationState.NoObservation, LoadQualificationTransition.None,
            null, null, null, null, null, 0, options.MinimumStableSamples,
            TimeSpan.Zero, false, false, "No load observation has been received.");

    void ResetState(bool awaitingPostGap)
    {
        currentBand = null;
        previousLoadWatts = null;
        previousTimestampUtc = null;
        previousElapsedSeconds = null;
        stableSinceUtc = null;
        stableSinceElapsedSeconds = null;
        stableSamples = 0;
        this.awaitingPostGap = awaitingPostGap;
    }
}

/// <summary>Legacy terminology alias for the hysteretic qualifier.</summary>
public sealed class LegacyLoadQualifier : HystereticLoadQualifier
{
    public LegacyLoadQualifier(LoadQualificationOptions? options = null)
        : base(options) { }
}

