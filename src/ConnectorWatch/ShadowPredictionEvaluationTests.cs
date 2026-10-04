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
        SyntheticRampUsesWallClockTimeAcrossGaps();
        SyntheticScenariosRemainFixedAsArchiveGrows();
        NativeAdvisoryIncludesRowsOutsideSyntheticWindow();
        IncompleteSyntheticWindowsRemainUnavailable();
        ScenarioIdentitiesIncludeSettingsAndFrozenAnchors();
        PowerAdvisoryEligibilityUsesFrozenProvenance();
        PowerReplayResetsOnUntrustedProvenance();
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
        Check(ramp is not null && ramp.ScenarioState == "AVAILABLE" &&
            ramp.RowsEvaluated == 360,
            "gradual connector-only ramp uses only the fixed six-hour heldout window");
        Check(ramp!.WindowStartTimestampUtc is DateTimeOffset start &&
            ramp.OnsetTimestampUtc == start.AddMinutes(30) &&
            ramp.WindowEndTimestampUtc == start.AddHours(6) &&
            ramp.RampDurationSeconds == 1800,
            "synthetic ramp records its fixed start, onset, exclusive end, and wall-clock duration");
        Check(ramp.ScenarioIdentity.Length == 64 && report.ModelIdentity ==
            ShadowPredictionEvaluation.ModelAlgorithmIdentity &&
            report.ConfigurationIdentity.Length == 64,
            "model, configuration, and scenario identities are explicit hashes or versions");
        Check(report.Advisory.SyntheticFaults.Any(x => x.Label == "STEP_200MV"),
            "post-fit connector step checks include the 200mV case");
        Check(report.Advisory.Detail.Contains("live alert thresholds", StringComparison.OrdinalIgnoreCase),
            "experimental advisory does not alter live thresholds");
    }

    static void SyntheticRampUsesWallClockTimeAcrossGaps()
    {
        var onset = new DateTimeOffset(2026, 1, 6, 0, 30, 0, TimeSpan.Zero);
        var duration = TimeSpan.FromMinutes(30);
        Check(ShadowPredictionEvaluation.CalculateSyntheticDropV(.2, onset.AddSeconds(-1),
            onset, duration) == 0,
            "ramp has no effect before the fixed onset");
        Check(Math.Abs(ShadowPredictionEvaluation.CalculateSyntheticDropV(.2,
            onset.AddMinutes(15), onset, duration) - .1) < 1e-12,
            "ramp reaches half its final drop at 15 wall-clock minutes");
        Check(Math.Abs(ShadowPredictionEvaluation.CalculateSyntheticDropV(.2,
            onset.AddMinutes(30), onset, duration) - .2) < 1e-12 &&
            Math.Abs(ShadowPredictionEvaluation.CalculateSyntheticDropV(.2,
                onset.AddHours(2), onset, duration) - .2) < 1e-12,
            "ramp reaches and holds its final drop after exactly 30 wall-clock minutes, including gaps");
        var withGap = EvaluateFixture(7, withGap: true).Advisory.GradualRamp;
        Check(withGap is not null && withGap.ScenarioState == "AVAILABLE" &&
            withGap.RampDurationSeconds == 1800,
            "a heldout telemetry gap does not change the ramp duration");
    }

    static void SyntheticScenariosRemainFixedAsArchiveGrows()
    {
        var earlyRows = FixtureRows(7).ToList();
        string originalRows = JsonSerializer.Serialize(earlyRows);
        var early = Evaluate(earlyRows, 7);
        var later = Evaluate(FixtureRows(8), 8);
        var laterByLabel = later.Advisory.SyntheticFaults.ToDictionary(x => x.Label,
            StringComparer.Ordinal);
        foreach (var scenario in early.Advisory.SyntheticFaults)
        {
            var appended = laterByLabel[scenario.Label];
            Check(scenario.ScenarioIdentity == appended.ScenarioIdentity &&
                scenario.ScenarioState == appended.ScenarioState &&
                scenario.WindowStartTimestampUtc == appended.WindowStartTimestampUtc &&
                scenario.WindowEndTimestampUtc == appended.WindowEndTimestampUtc &&
                scenario.OnsetTimestampUtc == appended.OnsetTimestampUtc &&
                scenario.RowsEvaluated == appended.RowsEvaluated &&
                scenario.AvailablePredictions == appended.AvailablePredictions &&
                scenario.AdvisoryTransitions == appended.AdvisoryTransitions &&
                scenario.AttributableTransitions == appended.AttributableTransitions &&
                scenario.DetectionLatencySeconds == appended.DetectionLatencySeconds,
                "later archive days preserve fixed-window scenario identity and results");
        }
        Check(early.Coverage.TestRows < later.Coverage.TestRows,
            "archive extension adds native heldout rows");
        Check(originalRows == JsonSerializer.Serialize(earlyRows),
            "synthetic injection leaves source observations unchanged");
    }

    static void NativeAdvisoryIncludesRowsOutsideSyntheticWindow()
    {
        var rows = FixtureRows(7).ToList();
        var before = Evaluate(rows, 7);
        var outsideWindowRow = rows.First(x => x.TimestampUtc.Date == new DateTime(2026, 1, 1))
            with
            {
                TimestampUtc = new DateTimeOffset(2026, 1, 6, 6, 1, 0, TimeSpan.Zero),
            };
        rows.Add(outsideWindowRow);
        var after = Evaluate(rows, 7);
        var beforeRamp = before.Advisory.GradualRamp!;
        var afterRamp = after.Advisory.GradualRamp!;
        Check(after.Coverage.TestRows == before.Coverage.TestRows + 1 &&
            after.Advisory.ObservedTestRows == before.Advisory.ObservedTestRows + 1,
            "native advisory replay counts a valid heldout row after the synthetic window");
        Check(beforeRamp.ScenarioIdentity == afterRamp.ScenarioIdentity &&
            beforeRamp.AdvisoryTransitions == afterRamp.AdvisoryTransitions &&
            beforeRamp.DetectionLatencySeconds == afterRamp.DetectionLatencySeconds,
            "an out-of-window heldout row does not change the fixed synthetic scenario");
    }

    static void IncompleteSyntheticWindowsRemainUnavailable()
    {
        var rows = FixtureRows(7).Where(row =>
        {
            if (row.TimestampUtc.Date < new DateTime(2026, 1, 6)) return true;
            if (row.TimestampUtc.Date == new DateTime(2026, 1, 6))
                return row.TimestampUtc.TimeOfDay <= TimeSpan.FromMinutes(45);
            return row.TimestampUtc.Date == new DateTime(2026, 1, 7) &&
                row.TimestampUtc.TimeOfDay <= TimeSpan.FromMinutes(9);
        }).Select(row => row with
        {
            TimestampUtc = row.TimestampUtc.Date == new DateTime(2026, 1, 6)
                ? new DateTimeOffset(2026, 1, 6, 20, 0, 0, TimeSpan.Zero)
                    .Add(row.TimestampUtc.TimeOfDay)
                : row.TimestampUtc.Date == new DateTime(2026, 1, 7)
                    ? new DateTimeOffset(2026, 1, 7, 1, 0, 0, TimeSpan.Zero)
                        .Add(row.TimestampUtc.TimeOfDay)
                    : row.TimestampUtc,
        }).ToList();
        var incomplete = Evaluate(rows, 7);
        Check(incomplete.Advisory.SyntheticFaults.Count == 5 &&
            incomplete.Advisory.SyntheticFaults.All(x => x.ScenarioState == "UNAVAILABLE" &&
                x.AttributableTransitions == 0 && x.DetectionLatencySeconds is null),
            "a heldout record that ends before the exclusive six-hour window end is unavailable");

        var noPostOnset = FixtureRows(7).Where(row =>
            row.TimestampUtc.Date < new DateTime(2026, 1, 6) ||
            row.TimestampUtc.Date == new DateTime(2026, 1, 6) &&
                (row.TimestampUtc.TimeOfDay <= TimeSpan.FromMinutes(20) ||
                 row.TimestampUtc.TimeOfDay == TimeSpan.FromHours(6)) ||
            row.TimestampUtc.Date == new DateTime(2026, 1, 7) &&
                row.TimestampUtc.TimeOfDay <= TimeSpan.FromMinutes(9)).ToList();
        var noPostOnsetReport = Evaluate(noPostOnset, 7);
        Check(noPostOnsetReport.Advisory.SyntheticFaults.All(x =>
            x.ScenarioState == "UNAVAILABLE" &&
            x.Detail.Contains("No baseline post-onset predictions", StringComparison.Ordinal)),
            "a complete time boundary with no available post-onset observation stays unavailable");

        var floorReport = Evaluate(FixtureRows(7), 7,
            Options() with { MinimumPracticalVoltageV = 20 });
        Check(floorReport.Advisory.SyntheticFaults.All(x =>
            x.ScenarioState == "UNAVAILABLE" &&
            x.Detail.Contains("practical-voltage floor", StringComparison.Ordinal)),
            "post-onset rows below the practical-voltage floor do not appear as available scenarios");
    }

    static void ScenarioIdentitiesIncludeSettingsAndFrozenAnchors()
    {
        var rows = FixtureRows(7).ToList();
        var baseline = Evaluate(rows, 7);
        var changedOptions = Options() with { SustainedPersistenceSeconds = 90 };
        var changedConfiguration = Evaluate(rows, 7, changedOptions);
        Check(baseline.ConfigurationIdentity != changedConfiguration.ConfigurationIdentity &&
            baseline.Advisory.SyntheticFaults[0].ScenarioIdentity !=
                changedConfiguration.Advisory.SyntheticFaults[0].ScenarioIdentity,
            "an advisory setting change changes configuration and scenario identities");

        var changedModelLabel = Evaluate(rows, 7, Options() with { ModelIdentity = "OTHER-SOURCE" });
        Check(baseline.Advisory.SyntheticFaults[0].ScenarioIdentity !=
            changedModelLabel.Advisory.SyntheticFaults[0].ScenarioIdentity,
            "a caller model identity change changes the scenario identity");

        var correctedAnchorRows = rows.Select(row =>
            row.TimestampUtc.Date == new DateTime(2026, 1, 1)
                ? row with { VoltageV = row.VoltageV + .01 }
                : row).ToList();
        var correctedAnchor = Evaluate(correctedAnchorRows, 7);
        Check(baseline.FrozenCutoffFingerprint != correctedAnchor.FrozenCutoffFingerprint &&
            baseline.Advisory.SyntheticFaults[0].ScenarioIdentity !=
                correctedAnchor.Advisory.SyntheticFaults[0].ScenarioIdentity,
            "corrected training or calibration anchor evidence breaks scenario identity");
    }

    static void PowerAdvisoryEligibilityUsesFrozenProvenance()
    {
        var earlyRows = PowerAdvisoryRows(7);
        var early = Evaluate(earlyRows, 7);
        var later = Evaluate(PowerAdvisoryRows(8), 8);
        Check(early.PowerBoardTemperature is { EligibleForRanking: true } &&
            later.PowerBoardTemperature is { EligibleForRanking: false },
            "a later derived-power cohort row changes full-heldout ranking eligibility");
        Check(early.Advisory.Model == "POWER_BOARD_TEMPERATURE" &&
            later.Advisory.Model == early.Advisory.Model &&
            early.Advisory.SyntheticFaults.Count == later.Advisory.SyntheticFaults.Count,
            "power advisory selection depends on training and calibration provenance only");

        var laterByLabel = later.Advisory.SyntheticFaults.ToDictionary(x => x.Label,
            StringComparer.Ordinal);
        foreach (var scenario in early.Advisory.SyntheticFaults)
        {
            var appended = laterByLabel[scenario.Label];
            Check(scenario.ScenarioIdentity == appended.ScenarioIdentity &&
                scenario.ScenarioState == appended.ScenarioState &&
                scenario.DetectionLatencySeconds == appended.DetectionLatencySeconds &&
                scenario.AttributableTransitions == appended.AttributableTransitions,
                "later unknown or derived power rows outside the fixed window preserve scenario results");
        }

        var untrustedAnchors = earlyRows.Select(row =>
            row.TimestampUtc.Date is var day &&
            (day == new DateTime(2026, 1, 4) || day == new DateTime(2026, 1, 5))
                ? row with { PowerProvenance = "Unknown" }
                : row).ToList();
        var unavailable = Evaluate(untrustedAnchors, 7);
        Check(unavailable.Advisory.Model == "none" &&
            unavailable.Advisory.SyntheticFaults.Count == 0,
            "unknown training or calibration power provenance disables the power advisory");
    }

    static void PowerReplayResetsOnUntrustedProvenance()
    {
        var rows = PowerAdvisoryRows(7).Select(row =>
        {
            if (row.TimestampUtc.Date != new DateTime(2026, 1, 6) ||
                row.TimestampUtc.TimeOfDay < TimeSpan.FromMinutes(30))
                return row;
            var shifted = row with { VoltageV = row.VoltageV - .5 };
            return row.TimestampUtc.TimeOfDay == TimeSpan.FromMinutes(32)
                ? shifted with { PowerProvenance = "Derived V*I" }
                : shifted;
        }).ToList();
        var report = Evaluate(rows, 7);
        var advisoryIndex = report.Advisory.Transitions.ToList().FindIndex(x =>
            x.State == "ADVISORY" && x.TimestampUtc <
                new DateTimeOffset(2026, 1, 6, 0, 32, 0, TimeSpan.Zero));
        var resetIndex = report.Advisory.Transitions.ToList().FindIndex(x =>
            x.State == "UNKNOWN" && x.TimestampUtc ==
                new DateTimeOffset(2026, 1, 6, 0, 32, 0, TimeSpan.Zero) &&
            x.Reason.Contains("provenance", StringComparison.Ordinal));
        Check(report.Advisory.Model == "POWER_BOARD_TEMPERATURE" &&
            advisoryIndex >= 0 && resetIndex > advisoryIndex,
            "a derived-power row resets an active advisory and clears replay continuity");

        var untrustedPostOnset = PowerAdvisoryRows(7).Select(row =>
            row.TimestampUtc.Date == new DateTime(2026, 1, 6) &&
            row.TimestampUtc.TimeOfDay >= TimeSpan.FromMinutes(30) &&
            row.TimestampUtc.TimeOfDay < TimeSpan.FromHours(6)
                ? row with { PowerProvenance = "Unknown" }
                : row).ToList();
        var noEvidence = Evaluate(untrustedPostOnset, 7);
        Check(noEvidence.Advisory.SyntheticFaults.All(x =>
            x.ScenarioState == "UNAVAILABLE" &&
            x.Detail.Contains("provenance", StringComparison.Ordinal)),
            "unknown power rows after onset reset continuity and add no scenario evidence");
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
        => Evaluate(rows, days, Options());

    static ShadowEvaluationReport Evaluate(IReadOnlyList<ShadowTelemetryRow> rows, int days,
        ShadowEvaluationOptions options)
    {
        var cutoff = new DateTimeOffset(2026, 1, days + 1, 0, 0, 0, TimeSpan.Zero);
        return ShadowPredictionEvaluation.Evaluate(new ShadowReadResult(rows,
            Array.Empty<ShadowSourceFile>(), rows.Count, rows.Count, 0, 0, 0, 0, 0,
            false, cutoff, Array.Empty<string>()), options);
    }

    static ShadowEvaluationOptions Options() => new()
    {
        MinimumMinutesPerSupportedDay = 5,
        MinimumEvaluationRows = 5,
        MinimumPowerSpanW = 20,
        MinimumCurrentSpanA = 2,
        SustainedPersistenceSeconds = 60,
        GapResetMinutes = 5,
        ModelIdentity = "TEST-GPU",
        ConfigurationIdentity = "TEST-CONFIG",
    };

    static List<ShadowTelemetryRow> PowerAdvisoryRows(int days) => FixtureRows(days)
        .Select(row =>
        {
            int dayIndex = row.TimestampUtc.Day - 1;
            int phase = (int)row.TimestampUtc.TimeOfDay.TotalMinutes % 10;
            double measuredPower = 210 + phase * 27 + (phase % 2) * 3;
            string provenance = row.TimestampUtc.Date <= new DateTime(2026, 1, 7)
                ? "Native measured" : "Derived V*I";
            return row with
            {
                ConnectorPowerW = measuredPower,
                PowerProvenance = provenance,
                ConnectorCurrentA = dayIndex >= 3
                    ? row.ConnectorCurrentA + 60
                    : row.ConnectorCurrentA,
            };
        }).ToList();

    static IReadOnlyList<ShadowTelemetryRow> FixtureRows(int days, bool withGap = false)
    {
        var rows = new List<ShadowTelemetryRow>();
        for (int day = 0; day < days; day++)
        {
            int minuteCount = day >= 5 ? 361 : 10;
            for (int minute = 0; minute < minuteCount; minute++)
            {
                int minuteOffset = withGap && day >= 5 && minute >= 5
                    ? minute + 20 : minute;
                int phase = minute % 10;
                var timestamp = new DateTimeOffset(2026, 1, 1 + day, 0, 0, 0,
                    TimeSpan.Zero).AddMinutes(minuteOffset);
                double current = 18 + phase * 2.1 + day * .3 + (phase % 3) * .35;
                double power = 210 + phase * 27 + day * 4 + (phase % 2) * 3;
                double board = power + 35 + (phase % 4) * 4 + day * .5;
                double temperature = 38 + day * .8 + phase * .45 + (phase % 3) * .2;
                double pcie = 11.8 + phase * .025 + day * .01;
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
