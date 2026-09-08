namespace ConnectorWatch;

/// <summary>Offline fixtures for the phase-one telemetry contracts.  These
/// tests do not load a driver or write daemon files; the parent self-test can
/// invoke Run() when its shared wiring is ready.</summary>
public static class TelemetryPhaseOneTests
{
    public static void Run()
    {
        int passed = 0;
        void Check(bool condition, string name)
        {
            if (!condition) throw new Exception("FAILED: " + name);
            passed++;
        }

        var t0 = new DateTimeOffset(2026, 9, 9, 0, 0, 0, TimeSpan.Zero);

        // Acquisition health keeps process liveness, source quality and
        // detector availability separate.
        var statuses = new[]
        {
            (AcquisitionHealthStatus.HEALTHY, new AcquisitionHealthInput(true, true, true, true, true, true, false, true, true, t0)),
            (AcquisitionHealthStatus.STALE, new AcquisitionHealthInput(true, true, true, true, false, true, false, true, false, t0)),
            (AcquisitionHealthStatus.SOURCE_UNAVAILABLE, new AcquisitionHealthInput(true, true, false, true, true, true, false, true, false, t0)),
            (AcquisitionHealthStatus.TIMESTAMP_INVALID, new AcquisitionHealthInput(true, true, true, false, true, true, false, true, false, t0)),
            (AcquisitionHealthStatus.POWER_UNAVAILABLE, new AcquisitionHealthInput(true, true, true, true, true, false, false, true, false, t0)),
            (AcquisitionHealthStatus.NATIVE_FAILURE, new AcquisitionHealthInput(true, true, true, true, true, true, true, true, false, t0)),
            (AcquisitionHealthStatus.SENSOR_UNCHARACTERIZED, new AcquisitionHealthInput(true, true, true, true, true, true, false, false, false, t0)),
        };
        foreach (var (expected, input) in statuses)
            Check(AcquisitionHealth.Classify(input) == expected, expected.WireName() + " classification");
        var detector = DetectorAvailability.Unavailable("trend", DetectorAvailabilityReason.SOURCE_GAP, t0,
            detail: "source gap");
        var health = AcquisitionHealth.Evaluate(
            new AcquisitionHealthInput(true, true, true, true, true, true, false, true, false, t0,
                SampleAgeSeconds: 0.5, Source: "fixture"),
            new Dictionary<string, DetectorAvailability> { ["trend"] = detector });
        Check(health.MonitorRunning && health.MonitorAvailable && !health.AnalysisAvailable,
            "monitor and analysis availability are independent");
        Check(health.FreshnessKnown && health.CapabilityKnown,
            "health capability and freshness certainty are explicit");
        Check(health.Detectors["trend"].Reason == DetectorAvailabilityReason.SOURCE_GAP,
            "detector reason remains explicit");
        Check(AcquisitionHealthStatusExtensions.TryParse("sensor-uncharacterized", out var parsed) &&
            parsed == AcquisitionHealthStatus.SENSOR_UNCHARACTERIZED, "health wire parsing");
        var freshElectrical = ElectricalSample.FromLegacy(
            new Voltage(t0, 12.1, 440, "{}"), "fixture",
            FreshnessMetadata.SourceTimestamp(t0, t0, 5),
            powerIsExternalSensor: true);
        Check(AcquisitionHealth.FromElectricalSample(freshElectrical, t0, true, false,
            sensorCharacterized: true).Status == AcquisitionHealthStatus.HEALTHY,
            "fresh typed electrical health");
        var hostOnlyElectrical = freshElectrical with
        {
            Freshness = FreshnessMetadata.HostPoll(t0)
        };
        Check(AcquisitionHealth.FromElectricalSample(hostOnlyElectrical, t0, true, false,
            sensorCharacterized: true).Status == AcquisitionHealthStatus.SENSOR_UNCHARACTERIZED &&
            !AcquisitionHealth.FromElectricalSample(hostOnlyElectrical, t0, true, false,
                sensorCharacterized: true).FreshnessKnown,
            "host-only timestamp keeps freshness uncertainty visible");
        var futureElectrical = freshElectrical with
        {
            Freshness = FreshnessMetadata.SourceTimestamp(t0.AddSeconds(1), t0, 5)
        };
        Check(AcquisitionHealth.FromElectricalSample(futureElectrical, t0, true, false,
            sensorCharacterized: true).Status == AcquisitionHealthStatus.TIMESTAMP_INVALID,
            "future source timestamp is invalid");

        // Coverage: unavailable/unknown intervals stay in the denominator;
        // zero denominator is explicit no-data, never 100 percent.
        var coverage = new AnalysisCoverageTracker(TimeSpan.FromSeconds(10));
        var empty = coverage.Snapshot(t0);
        Check(!empty.CoveragePercent.HasValue && empty.ZeroDenominator == CoverageZeroDenominatorResult.NO_DATA,
            "zero denominator is no data");
        coverage.Record(CoverageObservation.UnanalyzedAt(t0, CoverageObservationReason.LEARNING, 1));
        coverage.Record(CoverageObservation.AnalyzedAt(t0.AddSeconds(1), 1));
        coverage.Record(CoverageObservation.UnknownLoadAt(t0.AddSeconds(2), 1));
        var mixed = coverage.Snapshot(t0.AddSeconds(2));
        Check(mixed.LoadedCount == 3 && mixed.EligibleLoadedCount == 1 && mixed.AnalyzedLoadedCount == 1,
            "coverage loaded and analyzed counts");
        Check(mixed.Denominator == 3 && Math.Abs(mixed.CoveragePercent!.Value - 100 / 3.0) < 1e-9,
            "unknown load remains denominator");
        Check(mixed.UnknownLoadGapCount == 1 && mixed.UnknownLoadGapSeconds == 1,
            "unknown load gap is visible");
        Check(mixed.CurrentUnanalyzedLoadedSeconds == 1 && mixed.LongestUnanalyzedLoadedSeconds == 1,
            "current and longest unanalyzed durations");
        Check(mixed.UnanalyzedReasons[CoverageObservationReason.UNKNOWN_LOAD] == 1,
            "coverage reason is retained");
        var expired = coverage.Snapshot(t0.AddSeconds(20));
        Check(expired.LoadedCount == 0 && !expired.CoveragePercent.HasValue,
            "horizon expiration returns no data");
        coverage.Reset(CoverageResetReason.GENUINE_GAP, t0.AddSeconds(20));
        Check(coverage.Snapshot(t0.AddSeconds(20)).LastResetReason == CoverageResetReason.GENUINE_GAP,
            "coverage reset reason");
        var durationCoverage = new AnalysisCoverageTracker(10, CoverageDenominatorPolicy.LOADED_DURATION);
        durationCoverage.Record(CoverageObservation.UnanalyzedAt(t0, CoverageObservationReason.STALE, 2));
        durationCoverage.Record(CoverageObservation.AnalyzedAt(t0.AddSeconds(2), 6));
        var weighted = durationCoverage.Snapshot(t0.AddSeconds(2));
        Check(Math.Abs(weighted.Denominator - 8) < 1e-9 && Math.Abs(weighted.CoveragePercent!.Value - 75) < 1e-9,
            "duration denominator arithmetic");

        // Progress: age and frozen detection use monotonic time, while UTC
        // jumps remain diagnostic context.
        long tick = checked((long)MonotonicTime.Frequency);
        long progressStart = tick;
        var progress = new SamplingProgressTracker("instance-a", t0, progressStart,
            staleAfterSeconds: 5, startupGraceSeconds: 2);
        var noSample = progress.Snapshot(progressStart + tick, t0.AddDays(1));
        Check(noSample.State == SamplingProgressState.NO_SAMPLE && !noSample.Stalled,
            "live process without a completed sample has startup grace");
        long completed = progressStart + tick * 10;
        progress.CompleteSample(completed, t0);
        var running = progress.Snapshot(completed + tick * 2, t0.AddDays(-1));
        Check(running.SampleAgeSeconds is double age && Math.Abs(age - 2) < 1e-9 &&
            running.LastCompletedSampleMonotonic == completed,
            "sample age ignores UTC jump");
        var stalled = progress.Snapshot(completed + tick * 6, t0.AddDays(-2));
        Check(stalled.Stalled && stalled.State == SamplingProgressState.STALLED,
            "frozen progress is detectable");
        progress.MarkStopped("test");
        Check(progress.Snapshot(completed + tick * 10, t0).State == SamplingProgressState.STOPPED,
            "stopped progress state");
        var restarted = new SamplingProgressTracker("instance-b", t0.AddSeconds(1), tick * 20, 5);
        Check(restarted.Snapshot(tick * 20, t0).LastCompletedSampleMonotonic is null &&
            restarted.InstanceId != progress.InstanceId, "restart clears prior monotonic sample");
        var json = progress.SnapshotJson(completed + tick * 10, t0);
        Check(json.Contains("\"last_completed_sample_monotonic\"", StringComparison.Ordinal) &&
            SamplingProgressJson.Deserialize(json).InstanceId == "instance-a", "progress wire contract");

        // Poll timing/repetition: equal values are annotated, never treated
        // as proof of stale data.
        var timing = new PollTimingTracker();
        var first = timing.Record(10, 15, t0,
            new RawElectricalObservation(12.1, 36, 12.0, 4));
        var second = timing.Record(20, 25, t0.AddSeconds(1),
            new RawElectricalObservation(12.1, 36, 12.0, 4));
        var changed = timing.Record(30, 35, t0.AddSeconds(2),
            new RawElectricalObservation(12.0, 36, 12.0, 4));
        Check(first.IsFirstObservation && first.ConsecutiveIdenticalObservations == 1,
            "first poll timing observation");
        Check(second.IsRepeated && second.ConsecutiveIdenticalObservations == 2 && !second.ValuesChanged,
            "repeated values are counted");
        Check((changed.ValueChangeFlags & PollValueChangeFlags.ConnectorVoltage) != 0 &&
            changed.ConsecutiveIdenticalObservations == 1, "poll value change flags");
        var repetition = new RepeatedObservationTracker();
        repetition.Record(12.1, null, 12.0, null);
        var missingCurrent = repetition.Record(12.1, 0, 12.0, null);
        Check((missingCurrent.ChangeFlags & PollValueChangeFlags.ConnectorCurrent) != 0,
            "missing versus measured-zero is a change");
        var sameMissing = repetition.Record(12.1, 0, 12.0, null);
        Check(sameMissing.IsRepeated && sameMissing.ConsecutiveIdenticalObservations == 2,
            "dedicated repeated-observation tracker");
        bool backwardsRejected = false;
        try { timing.Record(40, 39, t0, new RawElectricalObservation()); }
        catch (ArgumentOutOfRangeException) { backwardsRejected = true; }
        Check(backwardsRejected, "backwards monotonic poll rejected");
        Check(NativePeriodPolicy.IsValidatedRate(1) && NativePeriodPolicy.IsValidatedRate(2) &&
            NativePeriodPolicy.IsValidatedRate(5) && !NativePeriodPolicy.IsValidatedRate(3),
            "native period experiment gate");
        Check(NativePeriodPolicy.TryValidate(2, true, out var period, out _) &&
            period == TimeSpan.FromMilliseconds(500), "validated native period");
        Check(!NativePeriodPolicy.TryValidate(2, false, out _, out _),
            "unstable native experiment rejected");

        // Persistence is duration based and resets on genuine gaps; UTC jumps
        // do not change the result.
        var persistence = new ElapsedPersistenceTracker(TimeSpan.FromSeconds(10), sampleMinimum: 3);
        long persistenceStart = tick * 30;
        Check(!persistence.Observe(true, persistenceStart, t0).Completed, "persistence starts at first sample");
        persistence.Observe(true, persistenceStart + tick * 5, t0.AddDays(2));
        Check(!persistence.Snapshot(persistenceStart + tick * 9, t0.AddDays(-2)).Completed,
            "persistence uses monotonic elapsed time");
        var complete = persistence.Observe(true, persistenceStart + tick * 10, t0.AddDays(-3));
        Check(complete.Completed && Math.Abs(complete.ElapsedSeconds - 10) < 1e-9 && complete.SampleCount == 3,
            "elapsed persistence completion");
        var gap = persistence.Observe(true, persistenceStart + tick * 11, t0, continuity: false);
        Check(!gap.Active && gap.ResetReason == PersistenceResetReason.GENUINE_GAP,
            "gap resets elapsed persistence");
        var cleared = persistence.Observe(false, persistenceStart + tick * 12, t0);
        Check(!cleared.Active && cleared.ResetReason == PersistenceResetReason.CONDITION_CLEARED,
            "condition clear resets persistence");

        // The same four-second condition confirms at both 1 Hz and 5 Hz;
        // changing the poll cadence does not change elapsed-time semantics.
        var oneHz = new ElapsedPersistenceTracker(TimeSpan.FromSeconds(4));
        var fiveHz = new ElapsedPersistenceTracker(TimeSpan.FromSeconds(4));
        var oneStart = tick * 50;
        var fiveStart = tick * 60;
        for (int elapsedSecond = 0; elapsedSecond <= 4; elapsedSecond++)
            oneHz.Observe(true, oneStart + tick * elapsedSecond, t0.AddSeconds(elapsedSecond));
        for (int sample = 0; sample <= 20; sample++)
            fiveHz.Observe(true, fiveStart + tick * sample / 5, t0.AddSeconds(sample / 5.0));
        Check(oneHz.State.Completed && fiveHz.State.Completed &&
            Math.Abs(oneHz.State.ElapsedSeconds - fiveHz.State.ElapsedSeconds) < 1e-9,
            "poll-rate independent elapsed persistence");

        Console.WriteLine($"PASS: {passed} phase-one telemetry checks.");
    }
}
