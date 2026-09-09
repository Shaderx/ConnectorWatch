using System;
using System.Collections.Generic;
using System.Linq;

namespace ConnectorWatch.Gui;

/// <summary>
/// The daily evidence supplied to <see cref="DegradationConfidence"/>.
/// Values describe one UTC calendar day for one telemetry cohort and one
/// source/reference epoch.  This is operational evidence, rather than a
/// calibrated probability of damage.
/// </summary>
public record ConfidenceDay
{
    public DateTimeOffset Day { get; init; }
    public string Cohort { get; init; } = "";
    public string Epoch { get; init; } = "";
    public double? MedianDropMv { get; init; }
    public int MinuteCount { get; init; }
    public int ObservationCount { get; init; }
    public DateTimeOffset? FirstSample { get; init; }
    public DateTimeOffset? LastSample { get; init; }
    public double? MinLoad { get; init; }
    public double? MaxLoad { get; init; }
    public double? MedianLoad { get; init; }
    public double? P10Load { get; init; }
    public double? P90Load { get; init; }
    public double? AnchorMedianLoad { get; init; }
    public double? AnchorP10Load { get; init; }
    public double? AnchorP90Load { get; init; }
    public double BinWidth { get; init; } = 25;
    public bool Unverified { get; init; }
    public string Reason { get; init; } = "";
}

/// <summary>
/// A daily operational degradation-evidence score in the range 0..100.
/// A null score means that the day cannot support a comparable score.
/// </summary>
public record ConfidencePoint
{
    public DateTimeOffset Day { get; init; }
    public double? Score { get; init; }
    public string Epoch { get; init; } = "";
    public string Reason { get; init; } = "";
    public int EvidenceDays { get; init; }
    public double? MedianDropMv { get; init; }
    public bool Unverified { get; init; }
}

/// <summary>
/// Computes a bounded, explainable operational evidence score.  The score is
/// deliberately not a probability and makes no claim about connector damage.
/// </summary>
public static class DegradationConfidence
{
    public const int HistoryDays = 90;
    public const int TrailingDays = 7;
    public const int MinimumSupportedDays = 3;
    public const double MinimumSpanHours = 48;
    public const int MinimumMinutes = 10;
    public const int MinimumObservations = 50;
    public const double MinimumSampleSpanMinutes = 30;
    public const double DefaultBinWidth = 25;

    /// <summary>Returns whether one recorded day satisfies the day-level evidence policy.</summary>
    public static bool SupportsDay(ConfidenceDay day, string cohort)
    {
        if (day is null || string.IsNullOrWhiteSpace(cohort) ||
            !string.Equals(day.Cohort?.Trim(), cohort.Trim(), StringComparison.Ordinal)) return false;
        return Evaluate(UtcDay(day.Day), day, cohort.Trim(), day.Epoch?.Trim() ?? "").Supported;
    }

    /// <summary>
    /// Builds one point for every completed UTC day in the latest 90-day
    /// window.  Missing days are represented explicitly and never inherit a
    /// previous score.
    /// </summary>
    public static IReadOnlyList<ConfidencePoint> Build(
        IEnumerable<ConfidenceDay> days,
        string cohort,
        DateTimeOffset now,
        double shiftVolts)
    {
        if (days is null) throw new ArgumentNullException(nameof(days));
        if (string.IsNullOrWhiteSpace(cohort))
            throw new ArgumentException("A non-empty cohort is required.", nameof(cohort));
        if (!double.IsFinite(shiftVolts) || shiftVolts <= 0)
            throw new ArgumentOutOfRangeException(nameof(shiftVolts), "The shift threshold must be finite and positive.");

        string requestedCohort = cohort.Trim();
        DateTimeOffset today = UtcDay(now);
        DateTimeOffset firstDay = today.AddDays(-HistoryDays);
        DateTimeOffset lastDay = today.AddDays(-1);
        // Keep the six calendar days immediately before the visible chart so
        // the first completed day can have a full trailing-seven-day window.
        DateTimeOffset inputFirstDay = firstDay.AddDays(-(TrailingDays - 1));
        double thresholdMv = shiftVolts * 1000.0;
        if (!double.IsFinite(thresholdMv) || thresholdMv <= 0)
            throw new ArgumentOutOfRangeException(nameof(shiftVolts), "The millivolt shift threshold must be finite and positive.");
        double deadbandMv = thresholdMv / 4.0;

        // A caller may pass incremental/upserted daily rows more than once.
        // Keep one deterministic row per day and epoch.  Distinct epochs on a
        // day remain ambiguous and are intentionally not blended.
        var relevant = new List<(DateTimeOffset Date, ConfidenceDay Row, int Index)>();
        int inputIndex = 0;
        foreach (var row in days)
        {
            int index = inputIndex++;
            if (row is null) continue;
            DateTimeOffset date = UtcDay(row.Day);
            if (date < inputFirstDay || date > lastDay) continue;
            relevant.Add((date, row, index));
        }

        var daily = new Dictionary<DateTimeOffset, DailyState>();
        foreach (var group in relevant.GroupBy(item => item.Date).OrderBy(group => group.Key))
        {
            var cohortRows = group
                .Where(item => string.Equals(item.Row.Cohort?.Trim(), requestedCohort, StringComparison.Ordinal))
                .ToArray();
            if (cohortRows.Length == 0)
            {
                // Preserve a diagnostic for an explicitly present row whose
                // cohort is unknown.  Rows for a different known cohort are
                // simply outside this Build call's view.
                if (group.Any(item => string.IsNullOrWhiteSpace(item.Row.Cohort)))
                    daily[group.Key] = DailyState.Unsupported(group.Key, "", "CONTEXT_UNKNOWN", false);
                continue;
            }

            var byEpoch = cohortRows
                .GroupBy(item => item.Row.Epoch?.Trim() ?? "", StringComparer.Ordinal)
                .Select(epochGroup => SelectUpsert(epochGroup))
                .ToArray();

            if (byEpoch.Length != 1)
            {
                // Empty epochs are invalid context.  If there are several
                // rows, retain the stronger ambiguity diagnostic.
                daily[group.Key] = DailyState.Unsupported(
                    group.Key, "", "AMBIGUOUS_EPOCH", unverified: byEpoch.Any(item => item.Row.Unverified));
                continue;
            }

            var selected = byEpoch[0].Row;
            string epoch = selected.Epoch?.Trim() ?? "";
            daily[group.Key] = Evaluate(group.Key, selected, requestedCohort, epoch);
        }

        // Epoch labels are scoped to contiguous source/reference epochs.  A
        // later A after A->B->A starts a fresh run and cannot reuse the first
        // A's evidence merely because its label happens to be the same.
        string? priorEpoch = null;
        int epochRun = -1;
        foreach (var state in daily.Values.OrderBy(item => item.Day))
        {
            if (state.Epoch.Length == 0)
            {
                priorEpoch = null;
                epochRun++;
            }
            else if (!string.Equals(priorEpoch, state.Epoch, StringComparison.Ordinal))
            {
                priorEpoch = state.Epoch;
                epochRun++;
            }
            state.EpochRun = epochRun;
        }

        var points = new List<ConfidencePoint>(HistoryDays);
        for (DateTimeOffset date = firstDay; date <= lastDay; date = date.AddDays(1))
        {
            if (!daily.TryGetValue(date, out var target))
            {
                points.Add(new ConfidencePoint
                {
                    Day = date,
                    Score = null,
                    Reason = "MISSING",
                    EvidenceDays = 0,
                    MedianDropMv = null,
                    Epoch = "",
                    Unverified = false,
                });
                continue;
            }

            var evidence = target.EpochRun >= 0
                ? daily.Values
                    .Where(item => item.EpochRun == target.EpochRun &&
                        item.Day >= date.AddDays(-(TrailingDays - 1)) && item.Day <= date &&
                        item.Supported)
                    .OrderBy(item => item.Day)
                    .ToArray()
                : Array.Empty<DailyState>();

            int evidenceDays = evidence.Length;
            bool evidenceReady = evidenceDays >= MinimumSupportedDays &&
                evidence[^1].Day.Subtract(evidence[0].Day).TotalHours >= MinimumSpanHours;
            bool allUnverified = target.Unverified || evidence.Any(item => item.Unverified);

            if (!target.Supported)
            {
                points.Add(new ConfidencePoint
                {
                    Day = date,
                    Score = null,
                    Epoch = target.Epoch,
                    Reason = target.Reason,
                    EvidenceDays = evidenceDays,
                    MedianDropMv = target.MedianDropMv,
                    Unverified = allUnverified,
                });
                continue;
            }

            if (!evidenceReady)
            {
                points.Add(new ConfidencePoint
                {
                    Day = date,
                    Score = null,
                    Epoch = target.Epoch,
                    Reason = "INSUFFICIENT_EVIDENCE",
                    EvidenceDays = evidenceDays,
                    MedianDropMv = target.MedianDropMv,
                    Unverified = allUnverified,
                });
                continue;
            }

            double medianD = Median(evidence.Select(item => item.MedianDropMv!.Value).ToArray());
            double strength = Clamp(
                (medianD - deadbandMv) /
                (thresholdMv - deadbandMv), 0, 1);
            double persistence = evidence.Count(item => item.MedianDropMv!.Value > deadbandMv) /
                (double)evidenceDays;
            double maturity = Math.Min(evidenceDays / (double)TrailingDays, 1);
            double score = 100.0 * strength * persistence * maturity;
            // Avoid leaking a negative zero into serializers or display code.
            if (score == 0) score = 0;

            points.Add(new ConfidencePoint
            {
                Day = date,
                Score = score,
                Epoch = target.Epoch,
                Reason = "",
                EvidenceDays = evidenceDays,
                MedianDropMv = medianD,
                Unverified = allUnverified,
            });
        }
        return points;
    }

    static DailyState Evaluate(DateTimeOffset date, ConfidenceDay row,
        string requestedCohort, string epoch)
    {
        bool contextKnown = !string.IsNullOrWhiteSpace(requestedCohort) && epoch.Length > 0;
        if (!contextKnown)
            return DailyState.Unsupported(date, epoch, "CONTEXT_UNKNOWN", row.Unverified);

        string sourceReason = row.Reason?.Trim() ?? "";
        if (sourceReason.Length > 0)
            return DailyState.Unsupported(date, epoch, sourceReason, row.Unverified, Finite(row.MedianDropMv));

        double? medianDrop = Finite(row.MedianDropMv);
        if (!medianDrop.HasValue)
            return DailyState.Unsupported(date, epoch, "MEDIAN_UNAVAILABLE", row.Unverified);
        if (row.MinuteCount < MinimumMinutes || row.ObservationCount < MinimumObservations ||
            !HasSpan(row.FirstSample, row.LastSample))
            return DailyState.Unsupported(date, epoch, "INSUFFICIENT_DAY_EVIDENCE", row.Unverified, medianDrop);

        if (!LoadsComparable(row))
            return DailyState.Unsupported(date, epoch, "LOAD_NOT_COMPARABLE", row.Unverified, medianDrop);

        return DailyState.SupportedDay(date, epoch, medianDrop.Value, row.Unverified);
    }

    static bool LoadsComparable(ConfidenceDay row)
    {
        var values = new[]
        {
            row.MedianLoad, row.P10Load, row.P90Load,
            row.AnchorMedianLoad, row.AnchorP10Load, row.AnchorP90Load,
        };
        if (values.Any(value => !value.HasValue || !double.IsFinite(value.Value))) return false;
        if (!double.IsFinite(row.BinWidth) || row.BinWidth <= 0) return false;

        return Math.Abs(row.MedianLoad!.Value - row.AnchorMedianLoad!.Value) <= row.BinWidth / 4.0 &&
            Math.Abs(row.P10Load!.Value - row.AnchorP10Load!.Value) <= row.BinWidth / 2.0 &&
            Math.Abs(row.P90Load!.Value - row.AnchorP90Load!.Value) <= row.BinWidth / 2.0;
    }

    static bool HasSpan(DateTimeOffset? first, DateTimeOffset? last)
    {
        if (!first.HasValue || !last.HasValue) return false;
        var span = last.Value.ToUniversalTime() - first.Value.ToUniversalTime();
        return span.TotalMinutes >= MinimumSampleSpanMinutes;
    }

    static double? Finite(double? value) =>
        value is double number && double.IsFinite(number) ? number : null;

    static double Clamp(double value, double minimum, double maximum) =>
        value < minimum ? minimum : value > maximum ? maximum : value;

    static double Median(IReadOnlyList<double> values)
    {
        var sorted = values.OrderBy(value => value).ToArray();
        int middle = sorted.Length / 2;
        return sorted.Length % 2 == 0
            ? (sorted[middle - 1] + sorted[middle]) / 2.0
            : sorted[middle];
    }

    static DateTimeOffset UtcDay(DateTimeOffset value) =>
        new(value.UtcDateTime.Date, TimeSpan.Zero);

    static (ConfidenceDay Row, int Index) SelectUpsert(
        IEnumerable<(DateTimeOffset Date, ConfidenceDay Row, int Index)> source)
    {
        return source
            .OrderBy(item => item.Row.LastSample.HasValue ? item.Row.LastSample.Value.ToUniversalTime() : DateTimeOffset.MinValue)
            .ThenBy(item => item.Row.FirstSample.HasValue ? item.Row.FirstSample.Value.ToUniversalTime() : DateTimeOffset.MinValue)
            .ThenBy(item => item.Row.ObservationCount)
            .ThenBy(item => item.Row.MinuteCount)
            .ThenBy(item => item.Index)
            .Select(item => (item.Row, item.Index))
            .Last();
    }

    sealed class DailyState
    {
        public DateTimeOffset Day { get; }
        public string Epoch { get; }
        public string Reason { get; }
        public double? MedianDropMv { get; }
        public bool Supported { get; }
        public bool Unverified { get; }
        public int EpochRun { get; set; }

        DailyState(DateTimeOffset day, string epoch, string reason,
            double? medianDropMv, bool supported, bool unverified)
        {
            Day = day;
            Epoch = epoch;
            Reason = reason;
            MedianDropMv = medianDropMv;
            Supported = supported;
            Unverified = unverified;
        }

        public static DailyState SupportedDay(DateTimeOffset day, string epoch,
            double medianDropMv, bool unverified) =>
            new(day, epoch, "", medianDropMv, true, unverified);

        public static DailyState Unsupported(DateTimeOffset day, string epoch,
            string reason, bool unverified, double? medianDropMv = null) =>
            new(day, epoch, reason, medianDropMv, false, unverified);
    }
}
