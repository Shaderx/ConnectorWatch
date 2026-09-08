using System.Collections.ObjectModel;
using System.Text.Json.Serialization;

namespace ConnectorWatch;

/// <summary>Reason attached to every loaded observation that did not become a
/// detector sample.  Unknown-load/source-gap records remain visible in the
/// denominator under the default policy so missing data cannot look like
/// perfect coverage.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum CoverageObservationReason
{
    ANALYZED,
    LEARNING,
    SETTLING,
    SOURCE_GAP,
    STALE,
    UNSUPPORTED_LOAD,
    INSUFFICIENT_VARIATION,
    REFERENCE_UNAVAILABLE,
    UNKNOWN_LOAD,
    MONITOR_UNAVAILABLE,
    NATIVE_FAILURE,
    POWER_UNAVAILABLE,
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum CoverageDenominatorPolicy
{
    LOADED_OBSERVATIONS,
    LOADED_DURATION,
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum CoverageResetReason
{
    STARTUP,
    RESTART,
    SOURCE_CHANGED,
    GENUINE_GAP,
    HORIZON_ROLLOVER,
    EXPLICIT,
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum CoverageZeroDenominatorResult
{
    NO_DATA,
}

public static class CoverageObservationReasonExtensions
{
    public static CoverageObservationReason FromDetectorStatus(string? status) =>
        status?.Trim().ToUpperInvariant() switch
        {
            "NO_SHIFT_DETECTED" or "ANALYZED" => CoverageObservationReason.ANALYZED,
            "LEARNING_REFERENCE" or "LEARNING" => CoverageObservationReason.LEARNING,
            "LOAD_SETTLING" or "SETTLING" => CoverageObservationReason.SETTLING,
            "VOLTAGE_UNAVAILABLE" or "SOURCE_GAP" => CoverageObservationReason.SOURCE_GAP,
            "WAITING_FOR_FRESH_VOLTAGE" or "STALE" => CoverageObservationReason.STALE,
            "ANALYSIS_LOAD_UNAVAILABLE" or "UNSUPPORTED_LOAD" or "OUTSIDE_ANALYSIS_RANGE" => CoverageObservationReason.UNSUPPORTED_LOAD,
            "INSUFFICIENT_VARIATION" => CoverageObservationReason.INSUFFICIENT_VARIATION,
            "REFERENCE_UNAVAILABLE" or "WINDOW_WARMUP" => CoverageObservationReason.REFERENCE_UNAVAILABLE,
            "NATIVE_FAILURE" => CoverageObservationReason.NATIVE_FAILURE,
            "POWER_UNAVAILABLE" => CoverageObservationReason.POWER_UNAVAILABLE,
            _ => CoverageObservationReason.UNKNOWN_LOAD,
        };
}

/// <summary>One event in the coverage ledger.  DurationSeconds describes the
/// loaded interval ending at TimestampUtc; when zero, the tracker derives a
/// duration from adjacent timestamps.  Loaded can be true even when the
/// source/load is unknown: that is how an unavailable interval stays in the
/// coverage denominator.</summary>
public sealed record CoverageObservation
{
    public CoverageObservation(
        DateTimeOffset timestampUtc,
        bool loaded,
        bool eligible,
        bool analyzed,
        CoverageObservationReason reason,
        double durationSeconds = 0,
        bool loadKnown = true,
        string? detail = null)
    {
        if (!double.IsFinite(durationSeconds) || durationSeconds < 0)
            throw new ArgumentOutOfRangeException(nameof(durationSeconds));
        if (analyzed && !eligible)
            throw new ArgumentException("An analyzed observation must be eligible.", nameof(analyzed));
        if (analyzed && reason != CoverageObservationReason.ANALYZED)
            throw new ArgumentException("Analyzed observations must use the ANALYZED reason.", nameof(reason));
        TimestampUtc = timestampUtc.ToUniversalTime();
        Loaded = loaded;
        Eligible = eligible;
        Analyzed = analyzed;
        Reason = reason;
        DurationSeconds = durationSeconds;
        LoadKnown = loadKnown;
        Detail = detail ?? reason.ToString();
    }

    [JsonPropertyName("timestamp_utc")] public DateTimeOffset TimestampUtc { get; }
    [JsonPropertyName("loaded")] public bool Loaded { get; }
    [JsonPropertyName("eligible")] public bool Eligible { get; }
    [JsonPropertyName("analyzed")] public bool Analyzed { get; }
    [JsonPropertyName("reason")] public CoverageObservationReason Reason { get; }
    [JsonPropertyName("duration_seconds")] public double DurationSeconds { get; init; }
    [JsonPropertyName("load_known")] public bool LoadKnown { get; }
    [JsonPropertyName("detail")] public string Detail { get; }

    public bool IsLoaded => Loaded;
    public bool IsEligible => Eligible;
    public bool IsAnalyzed => Analyzed;
    public bool IsUnknownLoad => !LoadKnown || Reason == CoverageObservationReason.UNKNOWN_LOAD;

    public static CoverageObservation AnalyzedAt(DateTimeOffset timestampUtc,
        double durationSeconds = 0, string? detail = null) =>
        new(timestampUtc, true, true, true, CoverageObservationReason.ANALYZED,
            durationSeconds, loadKnown: true, detail: detail);

    public static CoverageObservation UnanalyzedAt(DateTimeOffset timestampUtc,
        CoverageObservationReason reason, double durationSeconds = 0,
        bool loaded = true, bool loadKnown = true, string? detail = null) =>
        new(timestampUtc, loaded, false, false, reason,
            durationSeconds, loadKnown, detail);

    public static CoverageObservation UnknownLoadAt(DateTimeOffset timestampUtc,
        double durationSeconds = 0, string? detail = null) =>
        UnanalyzedAt(timestampUtc, CoverageObservationReason.UNKNOWN_LOAD,
            durationSeconds, loaded: true, loadKnown: false, detail: detail);

    public static CoverageObservation FromDetectorStatus(DateTimeOffset timestampUtc,
        string? status, bool loaded = true, double durationSeconds = 0,
        bool? analyzed = null, string? detail = null)
    {
        var reason = CoverageObservationReasonExtensions.FromDetectorStatus(status);
        var isAnalyzed = analyzed ?? reason == CoverageObservationReason.ANALYZED;
        return isAnalyzed
            ? AnalyzedAt(timestampUtc, durationSeconds, detail)
            : UnanalyzedAt(timestampUtc, reason, durationSeconds, loaded,
                loadKnown: reason != CoverageObservationReason.UNKNOWN_LOAD, detail: detail);
    }
}

/// <summary>Immutable coverage values for one horizon.  Percent and Ratio are
/// null when the denominator is zero; ZeroDenominator explains that result.
/// The duration fields are numeric aliases for consumers that do not want to
/// parse a TimeSpan.</summary>
public sealed record AnalysisCoverageSnapshot(
    [property: JsonPropertyName("horizon_start_utc")] DateTimeOffset? HorizonStartUtc,
    [property: JsonPropertyName("horizon_end_utc")] DateTimeOffset? HorizonEndUtc,
    [property: JsonPropertyName("horizon_seconds")] double HorizonSeconds,
    [property: JsonPropertyName("denominator_policy")] CoverageDenominatorPolicy DenominatorPolicy,
    [property: JsonPropertyName("loaded_count")] long LoadedCount,
    [property: JsonPropertyName("eligible_loaded_count")] long EligibleLoadedCount,
    [property: JsonPropertyName("analyzed_loaded_count")] long AnalyzedLoadedCount,
    [property: JsonPropertyName("denominator")] double Denominator,
    [property: JsonPropertyName("analyzed_duration_seconds")] double AnalyzedDurationSeconds,
    [property: JsonPropertyName("loaded_duration_seconds")] double LoadedDurationSeconds,
    [property: JsonPropertyName("coverage_ratio")] double? CoverageRatio,
    [property: JsonPropertyName("coverage_percent")] double? CoveragePercent,
    [property: JsonPropertyName("zero_denominator")] CoverageZeroDenominatorResult? ZeroDenominator,
    [property: JsonPropertyName("current_unanalyzed_loaded_seconds")] double CurrentUnanalyzedLoadedSeconds,
    [property: JsonPropertyName("longest_unanalyzed_loaded_seconds")] double LongestUnanalyzedLoadedSeconds,
    [property: JsonPropertyName("unknown_load_gap_count")] long UnknownLoadGapCount,
    [property: JsonPropertyName("unknown_load_gap_seconds")] double UnknownLoadGapSeconds,
    [property: JsonPropertyName("unanalyzed_reasons")] IReadOnlyDictionary<CoverageObservationReason, long> UnanalyzedReasons,
    [property: JsonPropertyName("last_reset_reason")] CoverageResetReason? LastResetReason,
    [property: JsonPropertyName("last_reset_at_utc")] DateTimeOffset? LastResetAtUtc)
{
    public long EligibleCount => EligibleLoadedCount;
    public long AnalyzedCount => AnalyzedLoadedCount;
    public double? AnalysisCoverage => CoverageRatio;
    public double? AnalysisCoveragePercent => CoveragePercent;
    public double CurrentUnanalyzedLoadedDurationSeconds => CurrentUnanalyzedLoadedSeconds;
    public double LongestUnanalyzedLoadedDurationSeconds => LongestUnanalyzedLoadedSeconds;
    public TimeSpan CurrentUnanalyzedLoadedDuration =>
        TimeSpan.FromSeconds(CurrentUnanalyzedLoadedSeconds);
    public TimeSpan LongestUnanalyzedLoadedDuration =>
        TimeSpan.FromSeconds(LongestUnanalyzedLoadedSeconds);
    public bool HasDenominator => Denominator > 0;
    public bool HasUnknownLoadGap => UnknownLoadGapCount > 0;
    public string CoverageStatus => CoveragePercent.HasValue ? "AVAILABLE" : "NO_DATA";
}

/// <summary>Bounded, horizon-based coverage ledger.  The default denominator
/// is every loaded observation, including unknown-load/source-gap records. A
/// caller may opt into duration weighting while retaining the same honest
/// treatment of unavailable intervals.</summary>
public sealed class AnalysisCoverageTracker
{
    readonly TimeSpan horizon;
    readonly CoverageDenominatorPolicy denominatorPolicy;
    readonly Queue<CoverageObservation> observations = new();
    DateTimeOffset? lastTimestampUtc;
    CoverageResetReason lastResetReason = CoverageResetReason.STARTUP;
    DateTimeOffset? lastResetAtUtc;

    public AnalysisCoverageTracker(TimeSpan horizon,
        CoverageDenominatorPolicy denominatorPolicy = CoverageDenominatorPolicy.LOADED_OBSERVATIONS)
    {
        if (horizon <= TimeSpan.Zero || horizon.TotalSeconds > TimeSpan.FromDays(365).TotalSeconds)
            throw new ArgumentOutOfRangeException(nameof(horizon));
        if (!Enum.IsDefined(denominatorPolicy))
            throw new ArgumentOutOfRangeException(nameof(denominatorPolicy));
        this.horizon = horizon;
        this.denominatorPolicy = denominatorPolicy;
        lastResetAtUtc = null;
    }

    public AnalysisCoverageTracker(double horizonSeconds,
        CoverageDenominatorPolicy denominatorPolicy = CoverageDenominatorPolicy.LOADED_OBSERVATIONS)
        : this(TimeSpan.FromSeconds(horizonSeconds), denominatorPolicy) { }

    public TimeSpan Horizon => horizon;
    public double HorizonSeconds => horizon.TotalSeconds;
    public CoverageDenominatorPolicy DenominatorPolicy => denominatorPolicy;
    public int Count => observations.Count;

    public void Record(CoverageObservation observation)
    {
        if (observation is null) throw new ArgumentNullException(nameof(observation));
        if (lastTimestampUtc.HasValue && observation.TimestampUtc < lastTimestampUtc.Value)
            throw new ArgumentException("Coverage observations must be chronological.", nameof(observation));

        var effective = observation;
        if (observation.DurationSeconds <= 0 && lastTimestampUtc.HasValue)
        {
            var derived = (observation.TimestampUtc - lastTimestampUtc.Value).TotalSeconds;
            if (double.IsFinite(derived) && derived > 0 && derived <= horizon.TotalSeconds)
                effective = observation with { DurationSeconds = derived };
        }
        observations.Enqueue(effective);
        lastTimestampUtc = effective.TimestampUtc;
        Trim(effective.TimestampUtc);
    }

    /// <summary>Records a genuine gap and clears continuity.  The reset event
    /// itself is not added to the denominator.</summary>
    public void Reset(CoverageResetReason reason, DateTimeOffset atUtc)
    {
        if (!Enum.IsDefined(reason)) throw new ArgumentOutOfRangeException(nameof(reason));
        observations.Clear();
        lastTimestampUtc = null;
        lastResetReason = reason;
        lastResetAtUtc = atUtc.ToUniversalTime();
    }

    public AnalysisCoverageSnapshot Snapshot(DateTimeOffset nowUtc)
    {
        var now = nowUtc.ToUniversalTime();
        Trim(now);
        long loadedCount = 0, eligibleCount = 0, analyzedCount = 0, unknownCount = 0;
        double loadedDuration = 0, analyzedDuration = 0, currentUnanalyzed = 0,
            longestUnanalyzed = 0, unknownDuration = 0;
        var reasons = new Dictionary<CoverageObservationReason, long>();

        foreach (var observation in observations)
        {
            if (!observation.Loaded) continue;
            loadedCount++;
            loadedDuration += observation.DurationSeconds;
            if (observation.Eligible) eligibleCount++;
            if (observation.Analyzed)
            {
                analyzedCount++;
                analyzedDuration += observation.DurationSeconds;
                currentUnanalyzed = 0;
                continue;
            }

            currentUnanalyzed += observation.DurationSeconds;
            longestUnanalyzed = Math.Max(longestUnanalyzed, currentUnanalyzed);
            reasons.TryGetValue(observation.Reason, out var reasonCount);
            reasons[observation.Reason] = reasonCount + 1;
            if (observation.IsUnknownLoad)
            {
                unknownCount++;
                unknownDuration += observation.DurationSeconds;
            }
        }

        var denominator = denominatorPolicy == CoverageDenominatorPolicy.LOADED_OBSERVATIONS
            ? loadedCount
            : loadedDuration;
        var numerator = denominatorPolicy == CoverageDenominatorPolicy.LOADED_OBSERVATIONS
            ? analyzedCount
            : analyzedDuration;
        double? ratio = denominator > 0 ? Math.Clamp(numerator / denominator, 0, 1) : null;
        double? percent = ratio * 100;
        return new(
            observations.Count > 0 ? observations.Peek().TimestampUtc : null,
            observations.Count > 0 ? observations.Last().TimestampUtc : null,
            horizon.TotalSeconds,
            denominatorPolicy,
            loadedCount,
            eligibleCount,
            analyzedCount,
            denominator,
            analyzedDuration,
            loadedDuration,
            ratio,
            percent,
            ratio.HasValue ? null : CoverageZeroDenominatorResult.NO_DATA,
            currentUnanalyzed,
            longestUnanalyzed,
            unknownCount,
            unknownDuration,
            new ReadOnlyDictionary<CoverageObservationReason, long>(reasons),
            lastResetReason,
            lastResetAtUtc);
    }

    public AnalysisCoverageSnapshot Snapshot(DateTimeOffset nowUtc, bool resetHorizon)
    {
        if (resetHorizon) Reset(CoverageResetReason.HORIZON_ROLLOVER, nowUtc);
        return Snapshot(nowUtc);
    }

    public void Observe(CoverageObservation observation) => Record(observation);

    public void Gap(DateTimeOffset atUtc) => Reset(CoverageResetReason.GENUINE_GAP, atUtc);

    void Trim(DateTimeOffset referenceUtc)
    {
        var cutoff = referenceUtc - horizon;
        while (observations.Count > 0 && observations.Peek().TimestampUtc < cutoff)
            observations.Dequeue();
    }
}
