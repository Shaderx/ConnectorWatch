using System.Text.Json.Serialization;

namespace ConnectorWatch;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum PersistenceResetReason
{
    NONE,
    CONDITION_CLEARED,
    GENUINE_GAP,
    MONOTONIC_RESET,
    EXPLICIT,
}

/// <summary>Monotonic-duration persistence settings.  SampleMinimum is
/// optional: when absent, elapsed duration alone controls completion.</summary>
public sealed record ElapsedPersistencePolicy(
    double RequiredSeconds,
    int? SampleMinimum = null)
{
    public ElapsedPersistencePolicy(TimeSpan required,
        int? sampleMinimum = null) : this(required.TotalSeconds, sampleMinimum) { }

    public void Validate()
    {
        if (!double.IsFinite(RequiredSeconds) || RequiredSeconds < 0)
            throw new ArgumentOutOfRangeException(nameof(RequiredSeconds));
        if (SampleMinimum is < 1)
            throw new ArgumentOutOfRangeException(nameof(SampleMinimum));
    }
}

public sealed record ElapsedPersistenceSnapshot(
    [property: JsonPropertyName("active")] bool Active,
    [property: JsonPropertyName("completed")] bool Completed,
    [property: JsonPropertyName("elapsed_seconds")] double ElapsedSeconds,
    [property: JsonPropertyName("sample_count")] int SampleCount,
    [property: JsonPropertyName("required_seconds")] double RequiredSeconds,
    [property: JsonPropertyName("sample_minimum")] int? SampleMinimum,
    [property: JsonPropertyName("started_monotonic")] long? StartedMonotonic,
    [property: JsonPropertyName("last_monotonic")] long? LastMonotonic,
    [property: JsonPropertyName("last_timestamp_utc")] DateTimeOffset? LastTimestampUtc,
    [property: JsonPropertyName("reset_reason")] PersistenceResetReason ResetReason)
{
    public TimeSpan Elapsed => TimeSpan.FromSeconds(ElapsedSeconds);
    public int Samples => SampleCount;
    public bool IsComplete => Completed;
}

/// <summary>
/// Tracks a condition using monotonic elapsed time.  UTC timestamps are kept
/// for diagnostics only and cannot accelerate or delay persistence.  Callers
/// mark a genuine source/load gap by passing continuity=false; repeated values
/// do not implicitly clear continuity because equal values are not a
/// freshness verdict.
/// </summary>
public sealed class ElapsedPersistenceTracker
{
    readonly ElapsedPersistencePolicy policy;
    long? startedMonotonic;
    long? lastMonotonic;
    DateTimeOffset? lastTimestampUtc;
    int samples;
    PersistenceResetReason resetReason = PersistenceResetReason.NONE;

    public ElapsedPersistenceTracker(ElapsedPersistencePolicy policy)
    {
        if (policy is null) throw new ArgumentNullException(nameof(policy));
        policy.Validate();
        this.policy = policy;
    }

    public ElapsedPersistenceTracker(TimeSpan required, int? sampleMinimum = null)
        : this(new ElapsedPersistencePolicy(required, sampleMinimum)) { }

    public ElapsedPersistencePolicy Policy => policy;
    public ElapsedPersistenceSnapshot State => Snapshot(lastMonotonic ?? 0, lastTimestampUtc);

    /// <summary>Adds one observation.  If condition is false or continuity is
    /// false, the current persistence run is reset before returning.</summary>
    public ElapsedPersistenceSnapshot Observe(
        bool condition,
        long monotonicTimestamp,
        DateTimeOffset? timestampUtc = null,
        bool continuity = true)
    {
        if (!condition)
        {
            Reset(PersistenceResetReason.CONDITION_CLEARED);
            return Snapshot(monotonicTimestamp, timestampUtc);
        }
        if (!continuity)
        {
            Reset(PersistenceResetReason.GENUINE_GAP);
            return Snapshot(monotonicTimestamp, timestampUtc);
        }
        if (lastMonotonic.HasValue && monotonicTimestamp < lastMonotonic.Value)
        {
            Reset(PersistenceResetReason.MONOTONIC_RESET);
            return Snapshot(monotonicTimestamp, timestampUtc);
        }

        startedMonotonic ??= monotonicTimestamp;
        lastMonotonic = monotonicTimestamp;
        lastTimestampUtc = timestampUtc?.ToUniversalTime();
        samples++;
        resetReason = PersistenceResetReason.NONE;
        return Snapshot(monotonicTimestamp, timestampUtc);
    }

    public ElapsedPersistenceSnapshot Observe(
        bool condition,
        PollTimingObservation poll,
        bool continuity = true) =>
        poll is null
            ? throw new ArgumentNullException(nameof(poll))
            : Observe(condition, poll.PollEndMonotonic, poll.HostTimestampUtc, continuity);

    /// <summary>Reads progress between observations without changing the
    /// condition state.  A backwards monotonic read reports zero elapsed
    /// rather than trusting a wall-clock delta.</summary>
    public ElapsedPersistenceSnapshot Snapshot(long nowMonotonic,
        DateTimeOffset? nowUtc = null)
    {
        var elapsed = startedMonotonic.HasValue
            ? MonotonicTime.ElapsedSeconds(startedMonotonic.Value, nowMonotonic) ?? 0
            : 0;
        var completed = startedMonotonic.HasValue &&
            elapsed >= policy.RequiredSeconds &&
            (!policy.SampleMinimum.HasValue || samples >= policy.SampleMinimum.Value);
        return new(
            startedMonotonic.HasValue,
            completed,
            elapsed,
            samples,
            policy.RequiredSeconds,
            policy.SampleMinimum,
            startedMonotonic,
            lastMonotonic,
            lastTimestampUtc ?? nowUtc?.ToUniversalTime(),
            resetReason);
    }

    public void Reset(PersistenceResetReason reason = PersistenceResetReason.EXPLICIT)
    {
        if (!Enum.IsDefined(reason)) throw new ArgumentOutOfRangeException(nameof(reason));
        startedMonotonic = null;
        lastMonotonic = null;
        lastTimestampUtc = null;
        samples = 0;
        resetReason = reason;
    }
}
