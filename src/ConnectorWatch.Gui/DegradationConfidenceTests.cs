using System;
using System.Collections.Generic;
using System.Linq;

namespace ConnectorWatch.Gui;

public static class DegradationConfidenceTests
{
    public static void Run(Action<bool, string> report)
    {
        if (report is null) throw new ArgumentNullException(nameof(report));
        void Check(bool value, string name) => report(value, name);

        var now = new DateTimeOffset(2026, 9, 10, 12, 0, 0, TimeSpan.Zero);
        DateTimeOffset date(int daysAgo) => now.UtcDateTime.Date.AddDays(-daysAgo);

        ConfidenceDay Day(int daysAgo, double dropMv, string epoch = "epoch-a",
            string cohort = "GPU-A", bool unverified = false) => new()
        {
            Day = date(daysAgo), Cohort = cohort, Epoch = epoch,
            MedianDropMv = dropMv, MinuteCount = 10, ObservationCount = 50,
            FirstSample = date(daysAgo).AddHours(1),
            LastSample = date(daysAgo).AddHours(1).AddMinutes(30),
            MinLoad = 400, MaxLoad = 450,
            MedianLoad = 425, P10Load = 415, P90Load = 435,
            AnchorMedianLoad = 425, AnchorP10Load = 415, AnchorP90Load = 435,
            Unverified = unverified,
        };

        ConfidencePoint Point(IReadOnlyList<ConfidencePoint> points, int daysAgo) =>
            points.Single(point => point.Day == date(daysAgo));

        var fewDays = DegradationConfidence.Build(new[] { Day(3, 300), Day(1, 300) }, "GPU-A", now, .2);
        Check(fewDays.Count == 90 && Point(fewDays, 1).Score is null &&
            Point(fewDays, 1).EvidenceDays == 2,
            "Fewer than three supported days remain explicitly unscored");

        var duplicateInput = new[] { Day(3, 300), Day(2, 300), Day(2, 300), Day(1, 300) };
        var duplicateResult = DegradationConfidence.Build(duplicateInput, "GPU-A", now, .2);
        Check(Point(duplicateResult, 1).EvidenceDays == 3 &&
            Math.Abs(Point(duplicateResult, 1).Score!.Value - (100.0 * 3 / 7)) < .000001,
            "Duplicate daily rows produce one evidence vote");

        var stable = DegradationConfidence.Build(new[] { Day(3, 0), Day(2, 0), Day(1, 0) }, "GPU-A", now, .2);
        Check(Point(stable, 1).Score == 0 && Point(stable, 1).EvidenceDays == 3,
            "Stable zero drop has zero degradation evidence");

        var sustained = Enumerable.Range(1, 7).Select(daysAgo => Day(daysAgo, 300));
        var sustainedResult = DegradationConfidence.Build(sustained, "GPU-A", now, .2);
        Check(Math.Abs(Point(sustainedResult, 1).Score!.Value - 100) < .000001 &&
            Point(sustainedResult, 1).EvidenceDays == 7,
            "Sustained increase reaches full mature evidence score");

        var noisy = DegradationConfidence.Build(
            new[] { Day(3, 300), Day(2, 1000), Day(1, 300) }, "GPU-A", now, .2);
        var steady = DegradationConfidence.Build(
            new[] { Day(3, 300), Day(2, 300), Day(1, 300) }, "GPU-A", now, .2);
        Check(Math.Abs(Point(noisy, 1).MedianDropMv!.Value - 300) < .000001 &&
            Math.Abs(Point(noisy, 1).Score!.Value - Point(steady, 1).Score!.Value) < .000001,
            "One noisy daily outlier cannot spike trailing median confidence");

        var recoveryRows = Enumerable.Range(5, 3).Select(daysAgo => Day(daysAgo, 300))
            .Concat(Enumerable.Range(1, 4).Select(daysAgo => Day(daysAgo, 0)));
        var recoveryResult = DegradationConfidence.Build(recoveryRows, "GPU-A", now, .2);
        Check(Point(recoveryResult, 2).Score > 0 && Point(recoveryResult, 1).Score == 0 &&
            Point(recoveryResult, 1).MedianDropMv == 0,
            "A decrease in the current drop reduces the current evidence score");

        var gradualRecoveryRows = Enumerable.Range(7, 7).Select(daysAgo => Day(daysAgo, 300))
            .Concat(Enumerable.Range(1, 6).Select(daysAgo => Day(daysAgo, 0)));
        var gradualRecovery = DegradationConfidence.Build(gradualRecoveryRows, "GPU-A", now, .2);
        Check(Point(gradualRecovery, 7).Score > Point(gradualRecovery, 6).Score &&
            Point(gradualRecovery, 6).Score > Point(gradualRecovery, 5).Score &&
            Point(gradualRecovery, 5).Score > Point(gradualRecovery, 4).Score,
            "Recovery evidence declines gradually as low-drop days enter the window");

        var gapRows = new[] { Day(7, 300), Day(5, 300), Day(1, 300) };
        var gapResult = DegradationConfidence.Build(gapRows, "GPU-A", now, .2);
        Check(Point(gapResult, 6).Score is null && Point(gapResult, 6).Reason == "MISSING" &&
            Point(gapResult, 1).Score is not null && Point(gapResult, 1).EvidenceDays == 3,
            "Gaps stay null without carrying a previous score");

        var resetRows = new[]
        {
            Day(7, 300, "A"), Day(6, 300, "A"),
            Day(5, 300, "B"), Day(4, 300, "B"),
            Day(3, 300, "A"), Day(2, 300, "A"), Day(1, 300, "A"),
        };
        var resetResult = DegradationConfidence.Build(resetRows, "GPU-A", now, .2);
        Check(Point(resetResult, 1).Epoch == "A" && Point(resetResult, 1).EvidenceDays == 3,
            "Epoch reset prevents joining the earlier epoch with the later same-named epoch");

        var currentExcluded = DegradationConfidence.Build(
            new[] { Day(4, 300), Day(3, 300), Day(2, 300), Day(1, 300), Day(0, 300) },
            "GPU-A", now, .2);
        Check(currentExcluded.Count == 90 && currentExcluded.All(point => point.Day < now.UtcDateTime.Date) &&
            Point(currentExcluded, 1).EvidenceDays == 4,
            "The in-progress UTC day is excluded from the completed-day series");

        var leftEdgeRows = Enumerable.Range(90, 7).Select(daysAgo => Day(daysAgo, 300));
        var leftEdge = DegradationConfidence.Build(leftEdgeRows, "GPU-A", now, .2);
        Check(Point(leftEdge, 90).EvidenceDays == 7 && Point(leftEdge, 90).Score == 100,
            "Input warmup before the visible range gives the leftmost day full maturity");

        var unknownContext = DegradationConfidence.Build(
            new[] { Day(3, 300, epoch: "", cohort: "GPU-A"), Day(2, 300), Day(1, 300) },
            "GPU-A", now, .2);
        Check(Point(unknownContext, 3).Score is null && Point(unknownContext, 3).Reason == "CONTEXT_UNKNOWN",
            "Unknown context is flagged instead of treated as a zero drop");

        var learning = DegradationConfidence.Build(
            new[] { Day(3, 300, epoch: "A", cohort: "GPU-A"),
                Day(2, 300, epoch: "A", cohort: "GPU-A"),
                Day(1, 300, epoch: "A", cohort: "GPU-A") with { Reason = "LEARNING_REFERENCE" } },
            "GPU-A", now, .2);
        Check(Point(learning, 1).Score is null && Point(learning, 1).Reason == "LEARNING_REFERENCE",
            "Learning days remain explicitly unscored");

        var unavailable = DegradationConfidence.Build(
            new[] { Day(3, 300), Day(2, 300), Day(1, 300) with { Reason = "TRUNCATED_UNAVAILABLE" } },
            "GPU-A", now, .2);
        Check(Point(unavailable, 1).Score is null &&
            Point(unavailable, 1).Reason == "TRUNCATED_UNAVAILABLE" &&
            Point(unavailable, 1).EvidenceDays == 2,
            "Truncated or unavailable days never score despite complete-looking counts");

        var loadMismatch = DegradationConfidence.Build(
            new[] { Day(3, 300), Day(2, 300), Day(1, 300) with { MedianLoad = 450 } },
            "GPU-A", now, .2);
        Check(Point(loadMismatch, 1).Score is null && Point(loadMismatch, 1).Reason == "LOAD_NOT_COMPARABLE" &&
            Point(loadMismatch, 1).EvidenceDays == 2,
            "Load-bin mismatch is excluded from evidence rather than scored as zero");

        bool rejected = false;
        try { DegradationConfidence.Build(Array.Empty<ConfidenceDay>(), "GPU-A", now, 0); }
        catch (ArgumentOutOfRangeException) { rejected = true; }
        Check(rejected, "Non-positive shift thresholds are rejected");
    }
}
