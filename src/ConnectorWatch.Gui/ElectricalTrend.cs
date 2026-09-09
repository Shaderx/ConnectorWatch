using System;
using System.Collections.Generic;
using System.Linq;

namespace ConnectorWatch.Gui;

public enum ElectricalTrendMode
{
    RecordedReference,
    InitialObservation,
}

/// <summary>A one-minute, fixed UTC bucket for the electrical degradation view.</summary>
public sealed record ElectricalTrendBucket(
    DateTimeOffset Time,
    DateTimeOffset Start,
    DateTimeOffset End,
    double? MedianDropMv,
    double? P05DropMv,
    double? P95DropMv,
    int Count,
    int RawObservationCount,
    int SegmentId,
    double? Reference,
    bool Supported,
    string Reason,
    string SourceIdentity,
    bool Unverified,
    double SpanSeconds,
    double? LoadMin,
    double? LoadMax)
{
    public bool ReferenceUnverified => Unverified;
    public bool LegacyUnverified => Unverified;
    public string Identity => SourceIdentity;
    public int ObservationCount => Count;
}

public sealed record ElectricalTrendResult(
    IReadOnlyList<ElectricalTrendBucket> Points,
    ElectricalTrendMode Mode,
    bool UseInitialObservation,
    DateTimeOffset? AnchorTime,
    double? AnchorReference,
    string ContextIdentity,
    int? Bin,
    string Reason)
{
    public IReadOnlyList<ElectricalTrendBucket> Buckets => Points;
    public string SourceIdentity => ContextIdentity;
    public bool Supported => Points.Any(point => point.Supported);
}

/// <summary>
/// Pure aggregation for the electrical degradation history.  It intentionally
/// operates on raw PointSample history and never reads the live Snapshot or a
/// current baseline.  A drop is signed reference-minus-observed voltage in mV:
/// positive means worse, zero means stable, and negative means recovery.
/// </summary>
public static class ElectricalTrend
{
    public const int BucketSeconds = 60;
    public const int MinimumObservations = 5;
    public const int MaximumBuckets = 24 * 60;

    static readonly HashSet<string> EligibleStatuses = new(StringComparer.OrdinalIgnoreCase)
    {
        "LEARNING_REFERENCE",
        "REFERENCE_UNVERIFIED",
        "WINDOW_WARMUP",
        "NO_SHIFT_DETECTED",
        "SUDDEN_DROOP",
        "BASELINE_SHIFT",
    };

    public static ElectricalTrendResult Build(IEnumerable<PointSample> samples,
        DateTimeOffset from, DateTimeOffset to, int? selectedBin, double gapSeconds,
        bool useInitialObservation = false)
    {
        if (samples is null) throw new ArgumentNullException(nameof(samples));
        if (!double.IsFinite(gapSeconds) || gapSeconds <= 0)
            throw new ArgumentOutOfRangeException(nameof(gapSeconds));

        var end = to.ToUniversalTime();
        var start = from.ToUniversalTime();
        if (end <= start)
            return Empty(useInitialObservation, null, selectedBin, "INVALID_RANGE");

        // Keep the aggregation bounded even when a caller accidentally passes a
        // wider history than the GUI can display.
        if ((end - start).TotalSeconds > MaximumBuckets * BucketSeconds)
            start = end.AddSeconds(-MaximumBuckets * BucketSeconds);

        var rawRows = new List<PointSample>();
        var rawCounts = new Dictionary<SampleKey, int>();
        foreach (var sample in samples)
        {
            if (sample is null) continue;
            var sampleTime = sample.Time.ToUniversalTime();
            if (sampleTime < start || sampleTime >= end || !double.IsFinite(sample.Voltage)) continue;
            rawRows.Add(sample);
            var sensorTime = sample.SensorTime.ToUniversalTime();
            var key = new SampleKey(NormalizeIdentity(sample.SourceIdentity), sensorTime);
            rawCounts.TryGetValue(key, out var count);
            rawCounts[key] = count + 1;
        }

        if (rawRows.Count == 0)
            return Empty(useInitialObservation, null, selectedBin, "NO_DATA");

        // One source timestamp represents one observation.  If replayed rows
        // disagree, the newest host observation wins deterministically while
        // RawObservationCount still reports how many rows were seen.
        var uniqueBySensor = new Dictionary<SampleKey, PointSample>();
        foreach (var sample in rawRows)
        {
            var sensorTime = sample.SensorTime.ToUniversalTime();
            var key = new SampleKey(NormalizeIdentity(sample.SourceIdentity), sensorTime);
            if (!uniqueBySensor.TryGetValue(key, out var previous) ||
                sample.Time.ToUniversalTime() > previous.Time.ToUniversalTime())
                uniqueBySensor[key] = sample;
        }
        var uniqueRows = uniqueBySensor.Values
            .OrderBy(sample => sample.Time.ToUniversalTime())
            .ThenBy(sample => sample.SensorTime.ToUniversalTime())
            .ToArray();

        int? bin = selectedBin ?? ChooseBin(uniqueRows, useInitialObservation);
        if (!bin.HasValue)
            return Empty(useInitialObservation, null, selectedBin, "NO_LOAD_BIN");

        // The latest context is the one visible at the right edge of the
        // selected load bin.  It is never combined with an older source/GPU or
        // analysis-unit context.
        string context = uniqueRows.LastOrDefault(sample => sample.Bin == bin.Value) is { } latest
            ? NormalizeIdentity(latest.SourceIdentity)
            : "Unknown";
        if (!uniqueRows.Any(sample => sample.Bin == bin.Value))
            return Empty(useInitialObservation, context, bin, "NO_LOAD_BIN");

        var mode = useInitialObservation ? ElectricalTrendMode.InitialObservation : ElectricalTrendMode.RecordedReference;
        var groups = new Dictionary<BucketKey, BucketAccumulator>();
        int segment = 0;
        bool seenTarget = false;
        bool previousEligible = false;
        DateTimeOffset? pendingBoundaryAt = null;
        DateTimeOffset previousTime = default;
        double? previousReference = null;

        foreach (var sample in uniqueRows)
        {
            if (sample.Bin != bin.Value || !string.Equals(NormalizeIdentity(sample.SourceIdentity), context, StringComparison.Ordinal))
            {
                if (seenTarget && sample.Time > previousTime) pendingBoundaryAt = sample.Time;
                continue;
            }

            bool eligible = IsEligible(sample, useInitialObservation);
            double? reference = !useInitialObservation && eligible ? Finite(sample.Reference) : null;
            bool boundary = !seenTarget || pendingBoundaryAt.HasValue && pendingBoundaryAt.Value < sample.Time ||
                (sample.Time.ToUniversalTime() - previousTime).TotalSeconds > gapSeconds ||
                previousEligible != eligible;
            if (!boundary && !useInitialObservation && eligible && previousEligible &&
                (!previousReference.HasValue || !reference.HasValue || previousReference.Value != reference.Value))
                boundary = true;
            if (boundary) segment++;

            var bucketStart = FloorMinute(sample.Time.ToUniversalTime());
            var key = new BucketKey(segment, bucketStart);
            if (!groups.TryGetValue(key, out var group))
            {
                group = new BucketAccumulator(bucketStart, segment, context);
                groups.Add(key, group);
            }
            rawCounts.TryGetValue(new SampleKey(context, sample.SensorTime.ToUniversalTime()), out var rawCount);
            group.AddRaw(sample, Math.Max(1, rawCount), eligible, reference, useInitialObservation);

            seenTarget = true;
            pendingBoundaryAt = null;
            previousEligible = eligible;
            previousReference = reference;
            previousTime = sample.Time.ToUniversalTime();
        }

        DateTimeOffset? anchorTime = null;
        double? anchorReference = null;
        if (useInitialObservation)
        {
            foreach (var group in groups.Values.OrderBy(value => value.Start).ThenBy(value => value.SegmentId))
            {
                if (group.Values.Count < MinimumObservations) continue;
                anchorTime = group.Start;
                anchorReference = Median(group.Values);
                break;
            }
        }

        var points = groups.Values
            .OrderBy(group => group.Start)
            .ThenBy(group => group.SegmentId)
            .Select(group => group.ToBucket(useInitialObservation, anchorReference))
            .ToList();

        // A reference transition or an ineligible row can create two records in
        // the same minute.  Keep each segment's evidence separate; the normal
        // 24-hour path remains at most 1440 intervals and never pools values.
        string reason = points.Count == 0 ? "NO_DATA" :
            points.Any(point => point.Supported) ? "" : points[0].Reason;
        return new(points, mode, useInitialObservation, anchorTime, anchorReference,
            context, bin, reason);
    }

    static ElectricalTrendResult Empty(bool initial, string? context, int? bin, string reason) =>
        new(Array.Empty<ElectricalTrendBucket>(),
            initial ? ElectricalTrendMode.InitialObservation : ElectricalTrendMode.RecordedReference,
            initial, null, null, NormalizeIdentity(context), bin, reason);

    static int? ChooseBin(IReadOnlyList<PointSample> rows, bool initial)
    {
        var candidates = rows.Where(sample => IsEligible(sample, initial) && sample.Bin.HasValue)
            .GroupBy(sample => sample.Bin!.Value)
            .Select(group => new { Bin = group.Key, Count = group.Count(), Latest = group.Max(sample => sample.Time.ToUniversalTime()) })
            .OrderByDescending(group => group.Count)
            .ThenByDescending(group => group.Latest)
            .FirstOrDefault();
        return candidates?.Bin ?? rows.Where(sample => sample.Bin.HasValue)
            .GroupBy(sample => sample.Bin!.Value)
            .OrderByDescending(group => group.Count())
            .ThenByDescending(group => group.Max(sample => sample.Time.ToUniversalTime()))
            .Select(group => (int?)group.Key)
            .FirstOrDefault();
    }

    static bool IsEligible(PointSample sample, bool initial)
    {
        if (!sample.Bin.HasValue || !EligibleStatuses.Contains(sample.Status?.Trim() ?? "")) return false;
        var health = sample.AcquisitionHealth?.Trim() ?? "";
        if (health.Length > 0 && !health.Equals("Unknown", StringComparison.OrdinalIgnoreCase) &&
            !health.Equals("HEALTHY", StringComparison.OrdinalIgnoreCase) &&
            !health.Equals("AVAILABLE", StringComparison.OrdinalIgnoreCase) &&
            !health.Equals("SENSOR_UNCHARACTERIZED", StringComparison.OrdinalIgnoreCase)) return false;
        var freshness = sample.FreshnessKind?.Trim() ?? "";
        if (freshness.Length > 0 && !freshness.Equals("Unknown", StringComparison.OrdinalIgnoreCase) &&
            (freshness.IndexOf("stale", StringComparison.OrdinalIgnoreCase) >= 0 ||
             freshness.IndexOf("unavailable", StringComparison.OrdinalIgnoreCase) >= 0 ||
             freshness.IndexOf("invalid", StringComparison.OrdinalIgnoreCase) >= 0 ||
             freshness.Equals("Missing", StringComparison.OrdinalIgnoreCase))) return false;
        if (!initial && !Finite(sample.Reference).HasValue) return false;
        return initial || !string.Equals(sample.Status?.Trim(), "LEARNING_REFERENCE", StringComparison.OrdinalIgnoreCase);
    }

    static double? Finite(double? value) => value is double number && double.IsFinite(number) ? number : null;
    static string NormalizeIdentity(string? value) => string.IsNullOrWhiteSpace(value) ? "Unknown" : value.Trim();
    static bool IsUnverified(PointSample sample) =>
        NormalizeIdentity(sample.SourceIdentity).Contains("Unknown", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(sample.Status?.Trim(), "REFERENCE_UNVERIFIED", StringComparison.OrdinalIgnoreCase) ||
        !string.Equals(sample.FreshnessKind, "VerifiedSourceTimestamp", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(sample.AcquisitionHealth?.Trim(), "SENSOR_UNCHARACTERIZED", StringComparison.OrdinalIgnoreCase);

    static DateTimeOffset FloorMinute(DateTimeOffset value)
    {
        var utc = value.UtcDateTime;
        long ticks = utc.Ticks - utc.Ticks % TimeSpan.TicksPerMinute;
        return new DateTimeOffset(new DateTime(ticks, DateTimeKind.Utc));
    }

    static double Quantile(IReadOnlyList<double> source, double q)
    {
        var values = source.OrderBy(value => value).ToArray();
        return QuantileSorted(values, q);
    }

    static double QuantileSorted(IReadOnlyList<double> values, double q)
    {
        if (values.Count == 0) return double.NaN;
        double index = (values.Count - 1) * q;
        int low = (int)Math.Floor(index);
        int high = (int)Math.Ceiling(index);
        return values[low] + (values[high] - values[low]) * (index - low);
    }

    static double Median(IReadOnlyList<double> values) => Quantile(values, .5);

    readonly record struct SampleKey(string SourceIdentity, DateTimeOffset SensorTime);
    readonly record struct BucketKey(int SegmentId, DateTimeOffset Start);

    sealed class BucketAccumulator
    {
        public DateTimeOffset Start { get; }
        public int SegmentId { get; }
        public string SourceIdentity { get; }
        public List<double> Values { get; } = new();
        readonly List<double> loads = new();
        double? recordedReference;
        int rawObservationCount;
        DateTimeOffset? firstTime;
        DateTimeOffset? lastTime;
        bool hasUnavailable;
        bool missingReference;
        bool legacyUnverified;
        string unavailableReason = "";

        public BucketAccumulator(DateTimeOffset start, int segmentId, string sourceIdentity)
        {
            Start = start;
            SegmentId = segmentId;
            SourceIdentity = sourceIdentity;
        }

        public void AddRaw(PointSample sample, int rawCount, bool eligible, double? reference, bool initial)
        {
            rawObservationCount += rawCount;
            var time = sample.Time.ToUniversalTime();
            firstTime = !firstTime.HasValue || time < firstTime.Value ? time : firstTime;
            lastTime = !lastTime.HasValue || time > lastTime.Value ? time : lastTime;

            if (!eligible)
            {
                hasUnavailable = true;
                if (!initial && !Finite(sample.Reference).HasValue)
                {
                    missingReference = true;
                    unavailableReason = "REFERENCE_UNAVAILABLE";
                }
                else if (unavailableReason.Length == 0)
                    unavailableReason = "SETTLING_OR_UNAVAILABLE";
                return;
            }

            if (IsUnverified(sample))
                legacyUnverified = true;
            if (sample.Power is double load && double.IsFinite(load)) loads.Add(load);
            if (initial)
                Values.Add(sample.Voltage); // volts; converted to mV after anchoring
            else if (reference is double recorded)
            {
                recordedReference = recorded;
                Values.Add((recorded - sample.Voltage) * 1000.0);
            }
        }

        public ElectricalTrendBucket ToBucket(bool initial, double? anchorReference)
        {
            List<double> drops;
            double? reference = initial ? anchorReference : recordedReference;
            if (initial && anchorReference is double anchor)
                drops = Values.Select(voltage => (anchor - voltage) * 1000.0).ToList();
            else drops = Values;

            bool supported = drops.Count >= MinimumObservations &&
                (!initial || anchorReference.HasValue);
            string reason = supported ? "" :
                drops.Count == 0 ? (missingReference ? "REFERENCE_UNAVAILABLE" :
                    hasUnavailable ? unavailableReason : initial ? "INITIAL_ANCHOR_PENDING" : "NO_DATA") :
                initial && !anchorReference.HasValue ? "INITIAL_ANCHOR_PENDING" :
                "INSUFFICIENT_OBSERVATIONS";
            double? median = null;
            double? p05 = null;
            double? p95 = null;
            if (supported)
            {
                var sortedDrops = drops.OrderBy(value => value).ToArray();
                median = QuantileSorted(sortedDrops, .5);
                p05 = QuantileSorted(sortedDrops, .05);
                p95 = QuantileSorted(sortedDrops, .95);
            }
            double? loadMin = loads.Count > 0 ? loads.Min() : null;
            double? loadMax = loads.Count > 0 ? loads.Max() : null;
            double span = firstTime.HasValue && lastTime.HasValue ?
                Math.Max(0, (lastTime.Value - firstTime.Value).TotalSeconds) : 0;
            var observationTime = firstTime.HasValue && lastTime.HasValue
                ? firstTime.Value.AddTicks((lastTime.Value - firstTime.Value).Ticks / 2)
                : Start.AddSeconds(BucketSeconds / 2.0);
            return new(observationTime, Start,
                Start.AddSeconds(BucketSeconds), median, p05, p95, drops.Count,
                rawObservationCount, SegmentId, reference, supported, reason,
                SourceIdentity, legacyUnverified, span, loadMin, loadMax);
        }
    }
}
