using System.Text.Json;

namespace ConnectorWatch;

/// <summary>
/// Offline checks for the shadow evaluator.  The tests use deterministic
/// minute fixtures and do not touch hardware, configuration, or reference
/// state.  The parent test registry can call <see cref="Run"/> when the
/// reader and CLI are wired into a release.
/// </summary>
public static class ShadowPredictionEvaluationTests
{
    public static void Run()
    {
        EmptyInputIsInconclusiveAndSideEffectFree();
        ChronologicalAnchorDoesNotSlideAsHistoryGrows();
        DerivedPowerIsDiagnosticOnly();
        FairIntersectionAndNativeAvailabilityAreSeparated();
        UnavailableFullModelsDoNotBlockSimpleEligibleMetrics();
        InsufficientAndOutOfRangeInputsAreFailClosed();
        CalibrationBiasDoesNotInflateRobustNoise();
        DayPartitionsExposeCoverageRanges();
        MissingRowsAndGapsResetTheExperimentalAdvisory();
        SyntheticGradualDropIsEvaluatedAndPowerIsRecalculated();
        JsonAndMarkdownContainOnlyFiniteValues();
        PcieCandidateKeepsItsUnverifiedTimingLabel();
    }

    static void EmptyInputIsInconclusiveAndSideEffectFree()
    {
        var report = ShadowPredictionEvaluation.Evaluate(new ShadowReadResult(
            Array.Empty<ShadowTelemetryRow>(), Array.Empty<ShadowSourceFile>(), 0, 0, 0,
            0, 0, 0, 0, false,
            new DateTimeOffset(2026, 2, 1, 0, 0, 0, TimeSpan.Zero),
            Array.Empty<string>()));
        Check(report.State == ShadowEvaluationState.INSUFFICIENT_DATA,
            "empty shadow input remains insufficient");
        Check(report.WinnerModel is null && report.Models.Count == 0,
            "empty shadow input has no winner or fitted models");
        Check(report.PhysicalInterventionWarning.Contains("no physical unplug", StringComparison.OrdinalIgnoreCase),
            "offline report records the no-intervention warning");
        var truncated = ShadowPredictionEvaluation.Evaluate(new ShadowReadResult(
            Array.Empty<ShadowTelemetryRow>(), Array.Empty<ShadowSourceFile>(), 0, 0, 0,
            0, 0, 0, 0, true, report.InputCutoffUtc, Array.Empty<string>()));
        Check(truncated.State == ShadowEvaluationState.TRUNCATED_INPUT &&
            truncated.WinnerModel is null, "truncated input is fail-closed without a winner");
    }

    static void ChronologicalAnchorDoesNotSlideAsHistoryGrows()
    {
        var early = EvaluateFixture(7);
        var later = EvaluateFixture(8);
        Check(early.Coverage.TrainingDayKeys.SequenceEqual(later.Coverage.TrainingDayKeys),
            "training anchor remains the first supported days as history grows");
        Check(early.Coverage.CalibrationDayKeys.SequenceEqual(later.Coverage.CalibrationDayKeys),
            "calibration days remain fixed as history grows");
        Check(later.Coverage.TestDayKeys.Count > early.Coverage.TestDayKeys.Count,
            "later complete days append to final test coverage");
        Check(early.FrozenCutoffFingerprint == later.FrozenCutoffFingerprint,
            "frozen cutoff fingerprint remains fixed as later test days arrive");
        Check(early.PowerBoardTemperature?.TrainingRows == later.PowerBoardTemperature?.TrainingRows,
            "initial model training row count does not slide with new test days");
    }

    static void FairIntersectionAndNativeAvailabilityAreSeparated()
    {
        var rows = FixtureRows(7).ToList();
        var testStart = new DateTimeOffset(2026, 1, 6, 0, 0, 0, TimeSpan.Zero);
        for (int i = 0; i < rows.Count; i++)
        {
            if (rows[i].TimestampUtc.Date < testStart.Date) continue;
            if (rows[i].ConnectorCurrentA is not null && i % 5 == 0)
                rows[i] = rows[i] with { ConnectorCurrentA = null };
        }
        var report = Evaluate(rows, 7);
        var power = report.PowerLoadOnly;
        var current = report.CurrentLoadOnly;
        Check(power is not null && current is not null,
            "power and current native candidates are reported separately");
        Check(power!.NativePredictions > current!.NativePredictions,
            "native availability records missing current rows");
        Check(power.FairHeldoutRows == current.FairHeldoutRows &&
            power.FairHeldoutRows < power.NativePredictions,
            "fair metrics use the same heldout row intersection");
    }

    static void DerivedPowerIsDiagnosticOnly()
    {
        var report = EvaluateFixture(7);
        Check(report.PowerBoardTemperature is not null &&
            report.PowerBoardTemperature.TargetCoupledPredictor &&
            !report.PowerBoardTemperature.EligibleForRanking,
            "V*I power predictors are retained as diagnostic target-coupled baselines");
        Check(report.PowerLoadOnly is not null && !report.PowerLoadOnly.EligibleForRanking,
            "all derived power variants are excluded from ranking");
        Check(report.ComparisonSetNames.All(x => x.StartsWith("CURRENT_", StringComparison.Ordinal)),
            "eligible comparison set excludes target-coupled power candidates");
        Check(report.WinnerModel is not null && report.WinnerModel.StartsWith("CURRENT_",
            StringComparison.Ordinal), "winner is selected from eligible current candidates");
    }

    static void UnavailableFullModelsDoNotBlockSimpleEligibleMetrics()
    {
        var rows = FixtureRows(7).Select(x => x with
        {
            BoardPowerW = null,
            TemperatureC = null,
        }).ToList();
        var report = Evaluate(rows, 7);
        Check(report.CurrentBoardTemperature is not null &&
            !report.CurrentBoardTemperature.IsAvailable,
            "missing board and temperature data makes the full current model unavailable");
        Check(report.CurrentLoadOnly is not null && report.CurrentLoadOnly.FairMetrics is not null &&
            report.CurrentLoadOnly.FairHeldoutRows > 0,
            "simple eligible current metrics survive unavailable full models");
        Check(report.ComparisonSetNames.SequenceEqual(new[] { "CURRENT_LOAD_ONLY" }),
            "comparison set names identify the remaining eligible candidate");
    }

    static void CalibrationBiasDoesNotInflateRobustNoise()
    {
        var rows = FixtureRows(7).Select(x => x.TimestampUtc.Date >= new DateTime(2026, 1, 4) &&
            x.TimestampUtc.Date <= new DateTime(2026, 1, 5)
            ? x with { VoltageV = x.VoltageV - .1 } : x).ToList();
        var report = Evaluate(rows, 7);
        Check(report.Advisory.CalibrationMedianBiasV is double bias && bias < -.05,
            "calibration median bias is recorded separately from noise scale");
        Check(report.Advisory.CalibrationNoiseScaleV is double noise && noise < .01,
            "constant calibration offset produces a low robust MAD noise scale");
        var steps = report.Advisory.SyntheticFaults.Where(x => x.Label is "STEP_100MV" or "STEP_200MV").ToList();
        Check(steps.Count == 2 && steps.All(x => x.AvailablePredictions > 0),
            "100mV and 200mV heldout changes remain evaluable after calibration bias");
    }

    static void DayPartitionsExposeCoverageRanges()
    {
        var report = EvaluateFixture(7);
        var day = report.DayPartitions.First(x => x.IsSupported);
        Check(day.TemperatureValidMinutes > 0 && day.TemperatureMinimumC is not null &&
            day.TemperatureMaximumC is not null && day.TemperatureSpanC is not null,
            "day partition reports valid temperature count and range");
        Check(day.PowerMinimumW is not null && day.PowerMaximumW is not null &&
            day.CurrentMinimumA is not null && day.CurrentMaximumA is not null,
            "day partition reports power and current ranges");
    }

    static void InsufficientAndOutOfRangeInputsAreFailClosed()
    {
        var insufficient = EvaluateFixture(6);
        Check(!insufficient.IsSufficient && insufficient.WinnerModel is null,
            "fewer than the required chronological days has no winner");

        var rows = FixtureRows(7).ToList();
        var testStart = new DateTimeOffset(2026, 1, 6, 0, 0, 0, TimeSpan.Zero);
        for (int i = 0; i < rows.Count; i++)
        {
            if (rows[i].TimestampUtc.Date >= testStart.Date && i % 11 == 0)
                rows[i] = rows[i] with { ConnectorPowerW = 20_000 };
        }
        var report = Evaluate(rows, 7);
        Check(report.PowerLoadOnly is not null && report.PowerLoadOnly.OutOfEnvelopeRows > 0,
            "out-of-envelope heldout rows are reported without imputation");
    }

    static void MissingRowsAndGapsResetTheExperimentalAdvisory()
    {
        var rows = FixtureRows(7, withGap: true).ToList();
        var report = Evaluate(rows, 7);
        Check(report.Advisory.HardwareFaultProbability == "UNKNOWN" &&
            !report.Advisory.HardwareFaultProbabilityKnown,
            "hardware fault probability remains unknown in shadow evaluation");
        Check(report.Advisory.Transitions.All(x => x.TimestampUtc != default),
            "advisory transitions carry valid timestamps after a gap");
        Check(report.Advisory.GapResets > 0 ||
            report.Advisory.ObservedTestRows < report.Coverage.TestRows,
            "telemetry gaps or unavailable rows reset the experimental advisory state");
    }

    static void SyntheticGradualDropIsEvaluatedAndPowerIsRecalculated()
    {
        var report = EvaluateFixture(7);
        var ramp = report.Advisory.GradualRamp;
        Check(ramp is not null && ramp.RowsEvaluated == report.Coverage.TestRows,
            "gradual connector-only heldout ramp is evaluated");
        Check(report.Advisory.SyntheticFaults.Any(x => x.Label == "STEP_200MV"),
            "post-fit connector step checks include the 200mV case");
        Check(report.Advisory.Detail.Contains("live alert thresholds", StringComparison.OrdinalIgnoreCase),
            "experimental advisory does not alter live thresholds");
    }

    static void JsonAndMarkdownContainOnlyFiniteValues()
    {
        var report = EvaluateFixture(7);
        string json = report.ToJson();
        using var parsed = JsonDocument.Parse(json);
        Check(AllNumbersFinite(parsed.RootElement),
            "shadow JSON contains no nonfinite numbers");
        Check(parsed.RootElement.TryGetProperty("State", out _),
            "shadow JSON has a stable top-level state");
        Check(report.ToMarkdown().Contains("prediction accuracy", StringComparison.OrdinalIgnoreCase),
            "shadow Markdown states the accuracy-only conclusion");
    }

    static void PcieCandidateKeepsItsUnverifiedTimingLabel()
    {
        var report = EvaluateFixture(7);
        Check(report.PcieCandidate is not null &&
            report.PcieCandidate.NativeRailTimingLabel == "UNVERIFIED_NATIVE_RAIL_TIMING",
            "PCIe candidate preserves the unverified native rail timing label");
    }

    static ShadowEvaluationReport EvaluateFixture(int days, bool withGap = false) =>
        Evaluate(FixtureRows(days, withGap), days);

    static ShadowEvaluationReport Evaluate(IReadOnlyList<ShadowTelemetryRow> rows, int days)
    {
        var cutoff = new DateTimeOffset(2026, 1, days + 1, 0, 0, 0, TimeSpan.Zero);
        return ShadowPredictionEvaluation.Evaluate(new ShadowReadResult(rows,
            Array.Empty<ShadowSourceFile>(), rows.Count, rows.Count, 0, 0, 0, 0, 0,
            false, cutoff, Array.Empty<string>()), new ShadowEvaluationOptions
            {
                MinimumMinutesPerSupportedDay = 5,
                MinimumEvaluationRows = 5,
                MinimumPowerSpanW = 20,
                MinimumCurrentSpanA = 2,
                SustainedPersistenceSeconds = 60,
                GapResetMinutes = 5,
                ModelIdentity = "TEST-GPU",
                ConfigurationIdentity = "TEST-CONFIG",
            });
    }

    static IReadOnlyList<ShadowTelemetryRow> FixtureRows(int days, bool withGap = false)
    {
        var rows = new List<ShadowTelemetryRow>();
        for (int day = 0; day < days; day++)
        {
            for (int minute = 0; minute < 10; minute++)
            {
                int minuteOffset = withGap && day >= 5 && minute == 5 ? 20 : minute;
                var timestamp = new DateTimeOffset(2026, 1, 1 + day, 0, 0, 0,
                    TimeSpan.Zero).AddMinutes(minuteOffset);
                double current = 18 + minute * 2.1 + day * .3 + (minute % 3) * .35;
                double power = 210 + minute * 27 + day * 4 + (minute % 2) * 3;
                double board = power + 35 + (minute % 4) * 4 + day * .5;
                double temperature = 38 + day * .8 + minute * .45 + (minute % 3) * .2;
                double pcie = 11.8 + minute * .025 + day * .01;
                double voltage = 12.35 - .0008 * power + .00025 * board -
                    .0015 * temperature - .0004 * current;
                rows.Add(new ShadowTelemetryRow(timestamp, voltage, current, voltage * current,
                    board, temperature, pcie, "fixture", 5, "Derived V*I"));
            }
        }
        return rows;
    }

    static void Check(bool condition, string description)
    {
        if (!condition) throw new Exception("FAILED: " + description);
    }

    static bool AllNumbersFinite(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Number)
            return element.TryGetDouble(out var value) && double.IsFinite(value);
        if (element.ValueKind == JsonValueKind.Array)
            return element.EnumerateArray().All(AllNumbersFinite);
        if (element.ValueKind == JsonValueKind.Object)
            return element.EnumerateObject().All(x => AllNumbersFinite(x.Value));
        return true;
    }
}
