using System.Text.Json;

namespace ConnectorWatch;

/// <summary>Deterministic, hardware-free checks for the replay contract.</summary>
public static class OfflineReplayTests
{
    public static void Run()
    {
        int passed = 0;
        void Check(bool ok, string name)
        {
            if (!ok) throw new Exception("FAILED: " + name);
            passed++;
        }

        var config = OfflineReplayFixtures.CreateConfig();
        config.Validate();
        foreach (var scenario in OfflineReplayFixtures.All())
        {
            scenario.Validate();
            Check(scenario.Samples.All(sample => sample.TimestampUtc >= OfflineReplayFixtures.Epoch),
                scenario.Name + " uses a fixed UTC epoch");

            var first = OfflineReplayRunner.Run(scenario, config);
            var second = OfflineReplayRunner.Run(scenario, config);
            Check(first.ToJson() == second.ToJson(), scenario.Name + " replay is deterministic");
            Check(first.Metrics.TotalSamples == scenario.Samples.Count,
                scenario.Name + " preserves sample count");
        }

        var healthy = OfflineReplayRunner.Run(OfflineReplayFixtures.Healthy(), config);
        Check(healthy.Metrics.ActualAlertSamples == 0 && healthy.Metrics.FalsePositiveSamples == 0,
            "healthy fixture has no detector alerts or false positives");
        Check(healthy.Metrics.StatusCounts.ContainsKey("NO_SHIFT_DETECTED"),
            "healthy fixture reaches comparison state");

        var noisy = OfflineReplayRunner.Run(OfflineReplayFixtures.Noisy(), config);
        Check(noisy.Metrics.ActualAlertSamples == 0 && noisy.Metrics.FalsePositiveSamples == 0,
            "bounded-noise fixture remains below alert thresholds");

        var gradual = OfflineReplayRunner.Run(OfflineReplayFixtures.GradualSag(), config);
        Check(gradual.Metrics.ExpectedAlertSamples > 0 && gradual.Metrics.ActualAlertSamples > 0,
            "gradual-sag fixture has expected and observed alert samples");
        Check(gradual.Metrics.DetectedAlertSegments == 1 &&
            gradual.Metrics.MissedAlertSegments == 0 &&
            gradual.Metrics.FirstDetectionLatencySeconds.HasValue,
            "gradual-sag fixture reports a detected segment and latency");

        var abrupt = OfflineReplayRunner.Run(OfflineReplayFixtures.AbruptDroop(), config);
        Check(abrupt.Metrics.TruePositiveSamples > 0 &&
            abrupt.Metrics.FirstDetectionLatencySeconds == 0,
            "abrupt-droop fixture detects on the first annotated sample");

        var missing = OfflineReplayRunner.Run(OfflineReplayFixtures.MissingAndStale(), config);
        Check(missing.Metrics.ExpectedUnavailableSamples == 7 &&
            missing.Metrics.AvailabilityTruePositiveSamples == 7 &&
            missing.Metrics.AvailabilityFalseNegativeSamples == 0,
            "missing/stale fixture reports every unavailable observation");
        Check(missing.Metrics.ActualUnavailableSamples == 7 &&
            missing.Metrics.AvailabilityFalsePositiveSamples == 0,
            "missing/stale fixture recovers without an unavailable false positive");
        Check(missing.Metrics.StatusCounts.ContainsKey("ANALYSIS_LOAD_UNAVAILABLE"),
            "missing/stale fixture distinguishes missing load from stale voltage");

        var restart = OfflineReplayRunner.Run(OfflineReplayFixtures.Restart(), config);
        Check(restart.Metrics.RestartCount == 1 && restart.Metrics.ActualAlertSamples == 0,
            "restart fixture preserves the reference without inventing an alert");
        int restartIndex = restart.Observations.FindIndex(row => row.RestartBefore);
        Check(restartIndex >= 0 && restart.Observations[restartIndex].Status == "LOAD_SETTLING",
            "restart requires fresh load qualification");

        var jsonRoundTrip = ReplayScenario.FromJson(OfflineReplayFixtures.Noisy().ToJson());
        Check(jsonRoundTrip.ToJson() == OfflineReplayFixtures.Noisy().ToJson(),
            "scenario JSON round-trip preserves deterministic fixture");
        var ndjsonRoundTrip = ReplayScenario.FromNdjson(OfflineReplayFixtures.Noisy().ToNdjson());
        Check(ndjsonRoundTrip.ToNdjson() == OfflineReplayFixtures.Noisy().ToNdjson(),
            "scenario NDJSON round-trip preserves deterministic fixture");

        var comparison = OfflineReplayRunner.Compare(OfflineReplayFixtures.GradualSag(),
        [
            new ReplayThresholdProfile("production"),
            new ReplayThresholdProfile("less-sensitive", ShiftVolts: .30,
                SuddenDroopVolts: .40, SustainSamples: 5),
        ]);
        Check(comparison.Comparisons.Count == 2 &&
            comparison.Comparisons[0].Metrics.Scenario == "gradual-sag",
            "threshold comparison returns named machine-readable profiles");
        Check(comparison.ToJson().Contains("less-sensitive", StringComparison.Ordinal),
            "threshold comparison JSON contains profile names");

        var parsedMetrics = JsonSerializer.Deserialize<ReplayMetrics>(healthy.MetricsJson(),
            OfflineReplayJson.Options(false));
        Check(parsedMetrics is not null && parsedMetrics.StatusCounts.ContainsKey("NO_SHIFT_DETECTED"),
            "metrics JSON round-trip preserves status counts");

        Console.WriteLine($"PASS: {passed} offline replay checks.");
    }
}

static class ReplayObservationListExtensions
{
    public static int FindIndex(this IReadOnlyList<ReplayObservation> rows,
        Func<ReplayObservation, bool> predicate)
    {
        for (int index = 0; index < rows.Count; index++)
            if (predicate(rows[index])) return index;
        return -1;
    }
}
