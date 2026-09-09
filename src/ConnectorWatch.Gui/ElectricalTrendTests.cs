using System;
using System.Collections.Generic;
using System.Linq;

namespace ConnectorWatch.Gui;

public static class ElectricalTrendTests
{
    public static void Run(Action<bool, string> report)
    {
        if (report is null) throw new ArgumentNullException(nameof(report));
        var origin = new DateTimeOffset(2026, 9, 9, 12, 0, 0, TimeSpan.Zero);

        PointSample Point(int seconds, double voltage, int bin = 425,
            double? reference = 12.1, string status = "NO_SHIFT_DETECTED",
            string identity = "GPU-A | connector | CONNECTOR_POWER | W") =>
            new(origin.AddSeconds(seconds), origin.AddSeconds(seconds), voltage, 12.0,
                425, bin, status, reference, null, reference - voltage, "")
            {
                SourceIdentity = identity,
                FreshnessKind = "VerifiedSourceTimestamp",
                AcquisitionHealth = "HEALTHY",
            };

        List<PointSample> Five(int start, double voltage, double? reference = 12.1,
            int bin = 425, string status = "NO_SHIFT_DETECTED",
            string identity = "GPU-A | connector | CONNECTOR_POWER | W") =>
            Enumerable.Range(0, 5).Select(index => Point(start + index * 10, voltage,
                bin, reference, status, identity)).ToList();

        void Check(bool value, string name) => report(value, name);
        bool Near(double? value, double expected) => value is double actual && Math.Abs(actual - expected) < .001;

        // The latest context is selected, but older GPU/source rows never
        // contribute to it even when their bin and timestamps match.
        var contexts = Five(5, 12.1, identity: "GPU-A | connector-a | CONNECTOR_POWER | W")
            .Concat(Five(65, 11.8, identity: "GPU-B | connector-b | CONNECTOR_POWER | W")).ToArray();
        var contextResult = ElectricalTrend.Build(contexts, origin, origin.AddMinutes(3), 425, 60);
        Check(contextResult.Points.Count == 1 && contextResult.ContextIdentity.StartsWith("GPU-B", StringComparison.Ordinal) &&
            contextResult.Points[0].Count == 5 && Near(contextResult.Points[0].MedianDropMv, 300),
            "same load bin does not pool source/GPU contexts");

        var referenceAndGap = Five(5, 12.1)
            .Concat(Five(65, 11.9, 12.0))
            .Concat(Five(245, 12.0, 12.0)).ToArray();
        var splitResult = ElectricalTrend.Build(referenceAndGap, origin, origin.AddMinutes(6), 425, 60);
        Check(splitResult.Points.Count == 3 && splitResult.Points.Select(point => point.SegmentId).Distinct().Count() == 3,
            "reference changes and genuine gaps create separate segments");
        Check(Near(splitResult.Points[0].MedianDropMv, 0) && Near(splitResult.Points[1].MedianDropMv, 100) &&
            Near(splitResult.Points[2].MedianDropMv, 0),
            "signed recorded-reference drops keep the expected sign and reference");
        Check(splitResult.Points[0].Reference == 12.1 && splitResult.Points[1].Reference == 12.0,
            "each segment exposes its historical reference");

        var recovery = Five(5, 12.1).Concat(Five(65, 12.2)).ToArray();
        var recoveryResult = ElectricalTrend.Build(recovery, origin, origin.AddMinutes(3), 425, 60);
        Check(Near(recoveryResult.Points[0].MedianDropMv, 0) && Near(recoveryResult.Points[1].MedianDropMv, -100),
            "stable voltage is zero and recovery is negative");

        var duplicateRows = Five(5, 12.0);
        var duplicate = duplicateRows[0] with { Time = origin.AddSeconds(7), Voltage = 11.5 };
        var duplicateResult = ElectricalTrend.Build(duplicateRows.Append(duplicate), origin,
            origin.AddMinutes(2), 425, 60);
        Check(duplicateResult.Points[0].Count == 5 && duplicateResult.Points[0].RawObservationCount == 6 &&
            Near(duplicateResult.Points[0].MedianDropMv, 100),
            "duplicate sensor timestamps are deduplicated while raw row evidence remains visible");

        var sparseResult = ElectricalTrend.Build(Five(5, 12.0).Take(4), origin,
            origin.AddMinutes(2), 425, 60);
        Check(!sparseResult.Points[0].Supported && sparseResult.Points[0].MedianDropMv is null &&
            sparseResult.Points[0].Reason == "INSUFFICIENT_OBSERVATIONS",
            "sparse buckets stay explicitly unsupported instead of becoming zero");

        var missingReference = Five(5, 12.0, null).ToArray();
        var missingResult = ElectricalTrend.Build(missingReference, origin, origin.AddMinutes(2), 425, 60);
        Check(!missingResult.Supported && missingResult.Points[0].Reason == "REFERENCE_UNAVAILABLE" &&
            missingResult.Points[0].MedianDropMv is null,
            "recorded-reference mode reports missing references explicitly");

        var initialRows = Five(5, 12.0, null, status: "LEARNING_REFERENCE")
            .Concat(Five(65, 11.8, null, status: "NO_SHIFT_DETECTED")).ToArray();
        var initialResult = ElectricalTrend.Build(initialRows, origin, origin.AddMinutes(3), 425, 60, true);
        Check(initialResult.Mode == ElectricalTrendMode.InitialObservation && initialResult.AnchorReference == 12.0 &&
            initialResult.Points.Count == 2 &&
            Near(initialResult.Points[0].MedianDropMv, 0) && Near(initialResult.Points[1].MedianDropMv, 200),
            "initial-observation mode uses one earliest qualifying bucket as its anchor");

        var learningOnly = ElectricalTrend.Build(Five(5, 12.0, 12.1, status: "LEARNING_REFERENCE"),
            origin, origin.AddMinutes(2), 425, 60);
        Check(!learningOnly.Supported && learningOnly.Points[0].Reason == "SETTLING_OR_UNAVAILABLE",
            "recorded-reference mode excludes reference-learning rows");

        var legacyHeaders = new[] { "timestamp_utc", "voltage_timestamp_utc", "input_voltage_v",
            "analysis_power_w", "bin_w", "status", "reference_v" };
        var legacyCells = new[] { origin.ToString("O"), origin.ToString("O"), "12.1", "425", "425",
            "REFERENCE_UNVERIFIED", "12.1" };
        var legacy = TelemetryStore.ParseSample(legacyHeaders, legacyCells);
        Check(legacy is not null && legacy.SourceIdentity == "Unknown" && legacy.FreshnessKind == "Unknown" &&
            legacy.AcquisitionHealth == "Unknown",
            "legacy samples receive explicit unknown provenance metadata");

        var unverified = Five(5, 12.0, 12.1, status: "REFERENCE_UNVERIFIED").ToArray();
        var unverifiedResult = ElectricalTrend.Build(unverified, origin, origin.AddMinutes(2), 425, 60);
        Check(unverifiedResult.Points[0].Supported && unverifiedResult.Points[0].Unverified,
            "reference-unverified rows remain usable and visibly flagged");
        var directRows = Five(5, 12.0).Select(p => p with { AcquisitionHealth = "SENSOR_UNCHARACTERIZED", FreshnessKind = "HostPollTimestampUnverified" }).ToArray();
        var directResult = ElectricalTrend.Build(directRows, origin, origin.AddMinutes(2), 425, 60, true);
        Check(directResult.Supported && directResult.Points.All(p => p.Unverified), "Direct NVIDIA readings remain usable without upgrading unverified timing");
        var unknownTiming = Five(5, 12.0).Select(p => p with { FreshnessKind = "Unknown" });
        Check(ElectricalTrend.Build(unknownTiming, origin, origin.AddMinutes(2), 425, 60, true).Points[0].Unverified,
            "Missing timing metadata cannot appear verified");
        var concurrentSources = Five(5, 12.1, identity: "GPU-A | a | CONNECTOR_POWER | W")
            .Concat(Five(5, 11.8, identity: "GPU-B | b | CONNECTOR_POWER | W"));
        var concurrentResult = ElectricalTrend.Build(concurrentSources, origin, origin.AddMinutes(2), 425, 60);
        Check(concurrentResult.Supported && concurrentResult.Points.Sum(p => p.Count) == 5 && Near(concurrentResult.Points[0].MedianDropMv, 300),
            "Concurrent source timestamps neither collide nor fragment the chosen source");
        var mixedBins = Five(5, 12.1).Concat(Five(65, 11.0, bin: 500));
        Check(Near(ElectricalTrend.Build(mixedBins, origin, origin.AddMinutes(3), 425, 60).Points[0].MedianDropMv, 0),
            "Other load bins cannot turn a steady selected load into degradation");
        var shortGap = Enumerable.Range(5, 5).Concat(Enumerable.Range(35, 5)).Select(t => Point(t, 12.0));
        var shortGapResult = ElectricalTrend.Build(shortGap, origin, origin.AddMinutes(1), 425, 5);
        Check(shortGapResult.Points.Count == 2 && shortGapResult.Points[0].SegmentId != shortGapResult.Points[1].SegmentId &&
            (shortGapResult.Points[1].Time - shortGapResult.Points[0].Time).TotalSeconds == 30,
            "Gaps within one minute retain separate plotted observation times");
    }
}
