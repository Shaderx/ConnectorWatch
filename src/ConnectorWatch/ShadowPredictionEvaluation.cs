using System.Collections.ObjectModel;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ConnectorWatch;

/// <summary>
/// The models evaluated by the offline shadow comparison.  The names are
/// deliberately explicit: a load proxy is never silently substituted for a
/// different sensor.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ShadowModelKind
{
    POWER_BOARD_TEMPERATURE,
    POWER_LOAD_ONLY,
    CURRENT_BOARD_TEMPERATURE,
    CURRENT_LOAD_ONLY,

    PowerBoardTemperature = POWER_BOARD_TEMPERATURE,
    PowerLoadOnly = POWER_LOAD_ONLY,
    CurrentBoardTemperature = CURRENT_BOARD_TEMPERATURE,
    CurrentLoadOnly = CURRENT_LOAD_ONLY,
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ShadowEvaluationState
{
    SUFFICIENT,
    INSUFFICIENT_DATA,
    TRUNCATED_INPUT,
    INVALID_INPUT,

    Sufficient = SUFFICIENT,
    InsufficientData = INSUFFICIENT_DATA,
    TruncatedInput = TRUNCATED_INPUT,
    InvalidInput = INVALID_INPUT,
}

/// <summary>
/// Bounded knobs for the shadow experiment.  They only affect the offline
/// report; they are intentionally not read from or written to live config.
/// </summary>
public sealed record ShadowEvaluationOptions
{
    public int MinimumTrainingDays { get; init; } = 3;
    public int MinimumCalibrationDays { get; init; } = 2;
    public int MinimumTestDays { get; init; } = 2;
    public int MinimumMinutesPerSupportedDay { get; init; } = 5;
    public double MinimumPowerSpanW { get; init; } = 25;
    public double MinimumCurrentSpanA { get; init; } = 2;
    public int MinimumEvaluationRows { get; init; } = 5;
    public int MinimumObservationsPerMinute { get; init; } = 1;
    public int MaximumTrainingSamples { get; init; } = 10_000;

    public double MinimumPracticalVoltageV { get; init; } = 10;
    public double MinimumSourceVoltageV { get; init; } = 6;
    public double MaximumSourceVoltageV { get; init; } = 16;
    public double MinimumAdvisoryDropV { get; init; } = .05;
    public double AdvisoryNoiseMultiplier { get; init; } = 3;
    public double EwmaHalfLifeMinutes { get; init; } = 5;
    public double SustainedPersistenceSeconds { get; init; } = 120;
    public double GapResetMinutes { get; init; } = 5;
    public double GradualRampFinalDropV { get; init; } = .2;
    public bool IncludePcieCandidate { get; init; } = true;

    /// <summary>Stable identifiers supplied by the offline reader.</summary>
    public string ModelIdentity { get; init; } = "unspecified";
    public string ConfigurationIdentity { get; init; } = "unspecified";

    internal void Validate()
    {
        if (MinimumTrainingDays < 1 || MinimumCalibrationDays < 1 ||
            MinimumTestDays < 1 || MinimumMinutesPerSupportedDay < 2 ||
            MinimumEvaluationRows < 1 || MinimumObservationsPerMinute < 1 ||
            MaximumTrainingSamples < 2)
            throw new ArgumentOutOfRangeException(nameof(MinimumTrainingDays),
                "Shadow day and sample requirements must be positive.");
        if (!FiniteNonNegative(MinimumPowerSpanW) || !FiniteNonNegative(MinimumCurrentSpanA) ||
            !FiniteNonNegative(MinimumPracticalVoltageV) ||
            !FinitePositive(MinimumSourceVoltageV) ||
            !FinitePositive(MaximumSourceVoltageV) ||
            MinimumSourceVoltageV >= MaximumSourceVoltageV ||
            !FinitePositive(MinimumAdvisoryDropV) ||
            !FinitePositive(AdvisoryNoiseMultiplier) || !FinitePositive(EwmaHalfLifeMinutes) ||
            !FinitePositive(SustainedPersistenceSeconds) || !FinitePositive(GapResetMinutes) ||
            !FiniteNonNegative(GradualRampFinalDropV))
            throw new ArgumentOutOfRangeException(nameof(MinimumPowerSpanW),
                "Shadow numeric requirements must be finite and positive where required.");
        if (string.IsNullOrWhiteSpace(ModelIdentity) ||
            string.IsNullOrWhiteSpace(ConfigurationIdentity))
            throw new ArgumentException("Shadow model and configuration identities are required.");

        static bool FinitePositive(double value) => double.IsFinite(value) && value > 0;
        static bool FiniteNonNegative(double value) => double.IsFinite(value) && value >= 0;
    }
}

public sealed record ShadowCurrentBandError(
    string Band,
    double MinimumCurrentA,
    double? MaximumCurrentA,
    int SampleCount,
    double MeanAbsoluteErrorV,
    double RootMeanSquareErrorV,
    double BiasV,
    double TailAbsoluteErrorV);

public sealed record ShadowErrorMetrics(
    int SampleCount,
    double MeanAbsoluteErrorV,
    double RootMeanSquareErrorV,
    double BiasV,
    double TailAbsoluteErrorV,
    IReadOnlyList<ShadowCurrentBandError> PerCurrentBand);

public sealed record ShadowDayPartition(
    string CohortKey,
    string UtcDate,
    int MinuteRows,
    int PowerMinutes,
    int CurrentMinutes,
    int PcieMinutes,
    double? PowerSpanW,
    double? CurrentSpanA,
    bool IsSupported,
    string Detail)
{
    public int TemperatureValidMinutes { get; init; }
    public int TemperatureValidCount => TemperatureValidMinutes;
    public double? TemperatureMinimumC { get; init; }
    public double? TemperatureMaximumC { get; init; }
    public double? TemperatureSpanC { get; init; }
    public double? PowerMinimumW { get; init; }
    public double? PowerMaximumW { get; init; }
    public double? CurrentMinimumA { get; init; }
    public double? CurrentMaximumA { get; init; }
}

public sealed record ShadowCoverage(
    string CohortKey,
    int EligibleMinuteRows,
    int SupportedDays,
    int TrainingDays,
    int CalibrationDays,
    int TestDays,
    int TrainingRows,
    int CalibrationRows,
    int TestRows,
    int FairHeldoutRows,
    IReadOnlyList<string> TrainingDayKeys,
    IReadOnlyList<string> CalibrationDayKeys,
    IReadOnlyList<string> TestDayKeys);

public sealed record ShadowModelEvaluation(
    ShadowModelKind Kind,
    string Name,
    string LoadProxy,
    bool IncludesBoardPower,
    bool IncludesTemperature,
    bool IsAvailable,
    string FitState,
    int TrainingRows,
    int CalibrationRows,
    int NativeHeldoutRows,
    int NativePredictions,
    int OutOfEnvelopeRows,
    ShadowErrorMetrics? NativeMetrics,
    ShadowErrorMetrics? FairMetrics,
    IReadOnlyList<string> TrainingDays,
    IReadOnlyList<string> CalibrationDays,
    IReadOnlyList<string> TestDays,
    string Detail)
{
    public bool TargetCoupledPredictor { get; init; }
    public bool EligibleForRanking { get; init; }
    public string PowerProvenance { get; init; } = "NOT_APPLICABLE";
    public string EligibilityDetail { get; init; } = "";
    public string ModelName => Name;
    public int FairHeldoutRows => FairMetrics?.SampleCount ?? 0;
    public string Availability => IsAvailable ? "AVAILABLE" : "UNAVAILABLE";
}

public sealed record ShadowAdvisoryTransition(
    DateTimeOffset TimestampUtc,
    string State,
    double? EwmaDropV,
    string Reason);

public sealed record ShadowSyntheticFaultEvaluation(
    string Label,
    double FinalInjectedDropV,
    int RowsEvaluated,
    int AvailablePredictions,
    int AdvisoryTransitions,
    double? TransitionsPerObservedHour,
    bool IsSensitive,
    string Detail)
{
    public DateTimeOffset? OnsetTimestampUtc { get; init; }
    public DateTimeOffset? FirstAdvisoryTimestampUtc { get; init; }
    public double? DetectionLatencySeconds { get; init; }
    public int AttributableTransitions { get; init; }
    public string ScenarioIdentity { get; init; } = "";
    public string ScenarioState { get; init; } = "UNAVAILABLE";
    public DateTimeOffset? WindowStartTimestampUtc { get; init; }
    public DateTimeOffset? WindowEndTimestampUtc { get; init; }
    public double? RampDurationSeconds { get; init; }
}

public sealed record ShadowAdvisoryEvaluation(
    bool IsAvailable,
    string Model,
    double? CalibrationNoiseScaleV,
    int CalibrationResiduals,
    int ObservedTestRows,
    double? ObservedHours,
    int AdvisoryTransitions,
    double? TransitionsPerObservedHour,
    string HardwareFaultProbability,
    bool HardwareFaultProbabilityKnown,
    IReadOnlyList<ShadowAdvisoryTransition> Transitions,
    IReadOnlyList<ShadowSyntheticFaultEvaluation> SyntheticFaults,
    string Detail)
{
    public int RowsBelowPracticalVoltageFloor { get; init; }
    public int GapResets { get; init; }
    public double? CalibrationMedianBiasV { get; init; }
    public string HardwareFaultProbabilityLabel => HardwareFaultProbability;
    public ShadowSyntheticFaultEvaluation? GradualRamp =>
        SyntheticFaults.FirstOrDefault(x => string.Equals(x.Label, "GRADUAL_RAMP",
            StringComparison.Ordinal));
}

/// <summary>
/// Optional PCIe rail candidate.  It is a separate regression because the
/// native differential feature enum deliberately has no PCIe rail feature.
/// </summary>
public sealed record ShadowPcieCandidateEvaluation(
    bool IsAvailable,
    string State,
    int TrainingRows,
    int CalibrationRows,
    int NativeHeldoutRows,
    int NativePredictions,
    int OutOfEnvelopeRows,
    ShadowErrorMetrics? Metrics,
    string NativeRailTimingLabel,
    string Detail)
{
    public string MethodLabel { get; init; } =
        "EXPERIMENTAL_UNIVARIATE_PREDICTION_NO_CORRECTION_CLAIM";
}

public sealed record ShadowInputInventory(
    long RowsRead,
    long EligibleRawRows,
    long InvalidRows,
    long RejectedRows,
    long DuplicateRows,
    long ConflictingRows,
    long PartialRows,
    bool Truncated,
    IReadOnlyList<ShadowSourceFile> Files);

public sealed class ShadowEvaluationReport
{
    public ShadowEvaluationState State { get; }
    public bool IsSufficient => State == ShadowEvaluationState.SUFFICIENT;
    public bool IsTruncated => State == ShadowEvaluationState.TRUNCATED_INPUT;
    public string? WinnerModel { get; }
    public string? Winner => WinnerModel;
    public string Conclusion { get; }
    public DateTimeOffset InputCutoffUtc { get; }
    public string FrozenCutoffFingerprint { get; }
    public string ModelIdentity { get; }
    public string ConfigurationIdentity { get; }
    public ShadowCoverage Coverage { get; }
    public IReadOnlyList<ShadowDayPartition> DayPartitions { get; }
    public IReadOnlyList<ShadowModelEvaluation> Models { get; }
    public ShadowAdvisoryEvaluation Advisory { get; }
    public ShadowPcieCandidateEvaluation? PcieCandidate { get; }
    public IReadOnlyList<string> Warnings { get; }
    public ShadowInputInventory InputInventory { get; }
    public IReadOnlyList<string> ComparisonSetNames { get; }
    public string CohortSelectionRule { get; } =
        "Most supported completed UTC days; ordinal cohort key breaks ties.";
    public int FairHeldoutRows => Coverage.FairHeldoutRows;
    public string PhysicalInterventionWarning { get; } =
        "Offline shadow evaluation only; no physical unplug or reseat is needed.";

    public ShadowModelEvaluation? PowerBoardTemperature =>
        Models.FirstOrDefault(x => x.Kind == ShadowModelKind.POWER_BOARD_TEMPERATURE);
    public ShadowModelEvaluation? PowerLoadOnly =>
        Models.FirstOrDefault(x => x.Kind == ShadowModelKind.POWER_LOAD_ONLY);
    public ShadowModelEvaluation? CurrentBoardTemperature =>
        Models.FirstOrDefault(x => x.Kind == ShadowModelKind.CURRENT_BOARD_TEMPERATURE);
    public ShadowModelEvaluation? CurrentLoadOnly =>
        Models.FirstOrDefault(x => x.Kind == ShadowModelKind.CURRENT_LOAD_ONLY);

    internal ShadowEvaluationReport(
        ShadowEvaluationState state,
        string? winnerModel,
        string conclusion,
        DateTimeOffset inputCutoffUtc,
        string frozenCutoffFingerprint,
        string modelIdentity,
        string configurationIdentity,
        ShadowCoverage coverage,
        IReadOnlyList<ShadowDayPartition> dayPartitions,
        IReadOnlyList<ShadowModelEvaluation> models,
        ShadowAdvisoryEvaluation advisory,
        ShadowPcieCandidateEvaluation? pcieCandidate,
        IReadOnlyList<string> warnings,
        ShadowInputInventory? inputInventory = null,
        IReadOnlyList<string>? comparisonSetNames = null)
    {
        State = state;
        WinnerModel = winnerModel;
        Conclusion = conclusion ?? string.Empty;
        InputCutoffUtc = inputCutoffUtc.ToUniversalTime();
        FrozenCutoffFingerprint = frozenCutoffFingerprint ?? string.Empty;
        ModelIdentity = modelIdentity ?? string.Empty;
        ConfigurationIdentity = configurationIdentity ?? string.Empty;
        Coverage = coverage ?? throw new ArgumentNullException(nameof(coverage));
        DayPartitions = new ReadOnlyCollection<ShadowDayPartition>(
            (dayPartitions ?? Array.Empty<ShadowDayPartition>()).ToList());
        Models = new ReadOnlyCollection<ShadowModelEvaluation>(
            (models ?? Array.Empty<ShadowModelEvaluation>()).ToList());
        Advisory = advisory ?? throw new ArgumentNullException(nameof(advisory));
        PcieCandidate = pcieCandidate;
        InputInventory = inputInventory ?? new ShadowInputInventory(0, 0, 0, 0, 0,
            0, 0, false, Array.Empty<ShadowSourceFile>());
        ComparisonSetNames = new ReadOnlyCollection<string>(
            (comparisonSetNames ?? Array.Empty<string>()).ToList());
        Warnings = new ReadOnlyCollection<string>(
            (warnings ?? Array.Empty<string>()).Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.Ordinal).ToList());
    }

    public string ToJson(bool indented = true)
    {
        var options = new JsonSerializerOptions
        {
            WriteIndented = indented,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            NumberHandling = JsonNumberHandling.Strict,
        };
        return JsonSerializer.Serialize(this, options);
    }

    public string ToMarkdown()
    {
        var builder = new StringBuilder();
        builder.AppendLine("# Shadow prediction evaluation");
        builder.AppendLine();
        builder.AppendLine($"- State: **{State}**");
        builder.AppendLine($"- Cohort: `{Coverage.CohortKey}`");
        builder.AppendLine($"- Frozen cutoff fingerprint: `{FrozenCutoffFingerprint}`");
        builder.AppendLine($"- Input cutoff: `{InputCutoffUtc:O}`");
        builder.AppendLine($"- Model identity: `{ModelIdentity}`");
        builder.AppendLine($"- Configuration identity: `{ConfigurationIdentity}`");
        builder.AppendLine($"- Winner: **{WinnerModel ?? "none"}**");
        builder.AppendLine($"- Source rows read: {InputInventory.RowsRead}; eligible raw rows: {InputInventory.EligibleRawRows}; truncated: {InputInventory.Truncated}.");
        builder.AppendLine($"- Comparison set: {(ComparisonSetNames.Count == 0 ? "none" : string.Join(", ", ComparisonSetNames))}.");
        builder.AppendLine($"- {PhysicalInterventionWarning}");
        builder.AppendLine();
        builder.AppendLine(Conclusion);
        builder.AppendLine();
        builder.AppendLine("## Chronological coverage");
        builder.AppendLine();
        builder.AppendLine($"Training days: {Coverage.TrainingDays}; calibration days: {Coverage.CalibrationDays}; test days: {Coverage.TestDays}; fair heldout rows: {Coverage.FairHeldoutRows}.");
        builder.AppendLine();
        builder.AppendLine("| UTC day | Minutes | Power range (W) | Current range (A) | Temperature valid/min-max/span (C) | Supported |");
        builder.AppendLine("| --- | ---: | --- | --- | --- | --- |");
        foreach (var partition in DayPartitions)
        {
            string range(double? minimum, double? maximum, double? span) =>
                minimum is double lo && maximum is double hi
                    ? $"{lo:0.##}..{hi:0.##} / {Format(span)}"
                    : "n/a";
            string temperature = $"{partition.TemperatureValidMinutes} / " +
                range(partition.TemperatureMinimumC, partition.TemperatureMaximumC,
                    partition.TemperatureSpanC);
            builder.AppendLine($"| {partition.UtcDate} | {partition.MinuteRows} | {range(partition.PowerMinimumW, partition.PowerMaximumW, partition.PowerSpanW)} | {range(partition.CurrentMinimumA, partition.CurrentMaximumA, partition.CurrentSpanA)} | {temperature} | {partition.IsSupported} |");
        }
        builder.AppendLine();
        builder.AppendLine("## Models");
        builder.AppendLine();
        builder.AppendLine("| Model | Status | Native rows | Native predictions | Out of envelope | Fair MAE (V) | Fair RMSE (V) | Fair bias (V) |");
        builder.AppendLine("| --- | --- | ---: | ---: | ---: | ---: | ---: | ---: |");
        foreach (var model in Models)
        {
            var metrics = model.FairMetrics;
            builder.AppendLine($"| {model.Name} | {model.Availability} | {model.NativeHeldoutRows} | {model.NativePredictions} | {model.OutOfEnvelopeRows} | {Format(metrics?.MeanAbsoluteErrorV)} | {Format(metrics?.RootMeanSquareErrorV)} | {Format(metrics?.BiasV)} |");
        }
        foreach (var model in Models.Where(x => !x.EligibleForRanking))
            builder.AppendLine($"{model.Name}: diagnostic only ({model.EligibilityDetail}).");
        builder.AppendLine();
        builder.AppendLine("## Advisory");
        builder.AppendLine();
        builder.AppendLine($"Model: {Advisory.Model}; transitions: {Advisory.AdvisoryTransitions}; transitions per observed hour: {Format(Advisory.TransitionsPerObservedHour)}; calibration median bias: {Format(Advisory.CalibrationMedianBiasV)} V; noise scale: {Format(Advisory.CalibrationNoiseScaleV)} V; hardware fault probability: **{Advisory.HardwareFaultProbability}**.");
        builder.AppendLine("Synthetic scenarios use the earliest heldout timestamp as a fixed window start, an onset 30 minutes later, and the exclusive end six hours after the start. Detection latency is elapsed wall-clock time and includes gaps.");
        if (Advisory.SyntheticFaults.Count > 0)
        {
            builder.AppendLine();
            builder.AppendLine("Synthetic connector-only checks:");
            foreach (var synthetic in Advisory.SyntheticFaults)
                builder.AppendLine($"- {synthetic.Label}: {synthetic.ScenarioState}, scenario `{synthetic.ScenarioIdentity}`, window `{FormatTimestamp(synthetic.WindowStartTimestampUtc)}` to exclusive `{FormatTimestamp(synthetic.WindowEndTimestampUtc)}`, onset `{FormatTimestamp(synthetic.OnsetTimestampUtc)}`, ramp={Format(synthetic.RampDurationSeconds)} s, {synthetic.AdvisoryTransitions} transitions, attributable={synthetic.AttributableTransitions}, latency={Format(synthetic.DetectionLatencySeconds)} s, sensitive={synthetic.IsSensitive}. {synthetic.Detail}");
        }
        if (PcieCandidate is not null)
        {
            builder.AppendLine();
            builder.AppendLine($"PCIe candidate: {PcieCandidate.State}; native timing label: `{PcieCandidate.NativeRailTimingLabel}`; method: exploratory univariate prediction with no correction claim.");
        }
        if (Warnings.Count > 0)
        {
            builder.AppendLine();
            builder.AppendLine("## Warnings");
            builder.AppendLine();
            foreach (var warning in Warnings) builder.AppendLine($"- {warning}");
        }
        return builder.ToString();

        static string Format(double? value) => value is double number && double.IsFinite(number)
            ? number.ToString("0.####", CultureInfo.InvariantCulture)
            : "n/a";
        static string FormatTimestamp(DateTimeOffset? value) => value is DateTimeOffset timestamp
            ? timestamp.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture)
            : "n/a";
    }
}

/// <summary>
/// Runs bounded chronological fits and heldout comparisons over immutable
/// minute rows.  This type has no hardware, configuration, reference, or
/// threshold side effects.
/// </summary>
public static class ShadowPredictionEvaluation
{
    public const string ModelAlgorithmIdentity = "shadow-regression-v2";
    public const int ScenarioOnsetOffsetSeconds = 30 * 60;
    public const int ScenarioWindowDurationSeconds = 6 * 60 * 60;
    public const int ScenarioRampDurationSeconds = 30 * 60;

    sealed record PreparedRow(ShadowTelemetryRow Row, DateTimeOffset TimestampUtc,
        DateTimeOffset MinuteUtc, string CohortKey, string DayKey);

    sealed record DayBucket(string CohortKey, string DayKey, IReadOnlyList<PreparedRow> Rows,
        int PowerMinutes, int CurrentMinutes, int PcieMinutes,
        double? PowerSpanW, double? CurrentSpanA,
        int TemperatureValidMinutes, double? TemperatureMinimumC,
        double? TemperatureMaximumC, double? TemperatureSpanC,
        double? PowerMinimumW, double? PowerMaximumW,
        double? CurrentMinimumA, double? CurrentMaximumA,
        bool IsSupported, string Detail);

    sealed record CandidateDefinition(ShadowModelKind Kind, string Name,
        DifferentialLoadProxy Proxy, bool IncludeBoardPower, bool IncludeTemperature);

    sealed record PowerProvenanceAssessment(string Label, bool HasDerived,
        bool HasUnknown, bool HasIndependent, bool Eligible, string Detail);

    sealed class CandidateRun
    {
        public required CandidateDefinition Definition { get; init; }
        public required DifferentialModelFitResult Fit { get; init; }
        public required IReadOnlyList<ShadowTelemetryRow> TrainingRows { get; init; }
        public required IReadOnlyList<ShadowTelemetryRow> CalibrationRows { get; init; }
        public required IReadOnlyList<ShadowTelemetryRow> TestRows { get; init; }
        public Dictionary<long, DifferentialPrediction> TestPredictions { get; } = new();
        public Dictionary<long, DifferentialPrediction> CalibrationPredictions { get; } = new();
        public int OutOfEnvelopeRows { get; set; }
        public bool TargetCoupledPredictor { get; set; }
        public bool EligibleForRanking { get; set; }
        public bool EligibleForAdvisory { get; set; }
        public string PowerProvenance { get; set; } = "NOT_APPLICABLE";
        public string EligibilityDetail { get; set; } = "";
    }

    sealed record ErrorPoint(PreparedRow Row, double ErrorV);

    sealed record PcieFit(double InterceptV, double SlopeVPerV, double MinimumPcieV,
        double MaximumPcieV);

    public static ShadowEvaluationReport Evaluate(
        ShadowReadResult input, ShadowEvaluationOptions? options = null)
    {
        if (input is null) throw new ArgumentNullException(nameof(input));
        var settings = options ?? new ShadowEvaluationOptions();
        settings.Validate();

        var cutoff = input.InputCutoffUtc == default
            ? DateTimeOffset.UtcNow
            : input.InputCutoffUtc.ToUniversalTime();
        string modelIdentity = ModelAlgorithmIdentity;
        string configurationIdentity = ComputeConfigurationIdentity(settings);
        var warnings = input.Warnings?.Where(x => !string.IsNullOrWhiteSpace(x)).ToList()
            ?? new List<string>();
        if (input.Truncated)
            warnings.Add("Input was truncated; no model comparison winner is declared.");
        if (input.PartialRows > 0)
            warnings.Add($"{input.PartialRows} partial source rows were reported by the reader.");
        if (input.ConflictingRows > 0)
            warnings.Add($"{input.ConflictingRows} conflicting source rows were reported by the reader.");

        var prepared = PrepareRows(input.Rows ?? Array.Empty<ShadowTelemetryRow>(), cutoff,
            settings, warnings);
        var buckets = BuildBuckets(prepared, settings);
        var selected = SelectCohort(buckets, settings);
        var allPartitions = buckets.Select(ToPublicPartition).ToList();
        var fingerprint = ComputeFingerprint(selected?.CohortKey ?? "none",
            selected is null ? Array.Empty<PreparedRow>() :
                selected.SupportedDays.Take(settings.MinimumTrainingDays + settings.MinimumCalibrationDays)
                    .SelectMany(x => x.Rows), settings);

        if (selected is null)
            return EmptyReport(input.Truncated ? ShadowEvaluationState.TRUNCATED_INPUT :
                ShadowEvaluationState.INSUFFICIENT_DATA, cutoff, fingerprint,
                settings, allPartitions, warnings, "No compatible cohort contains qualified completed UTC days.",
                BuildInventory(input), modelIdentity, configurationIdentity);

        var trainingDays = selected.SupportedDays.Take(settings.MinimumTrainingDays).ToList();
        var calibrationDays = selected.SupportedDays.Skip(settings.MinimumTrainingDays)
            .Take(settings.MinimumCalibrationDays).ToList();
        var testDays = selected.SupportedDays.Skip(settings.MinimumTrainingDays +
            settings.MinimumCalibrationDays).ToList();
        bool enoughDays = trainingDays.Count >= settings.MinimumTrainingDays &&
            calibrationDays.Count >= settings.MinimumCalibrationDays &&
            testDays.Count >= settings.MinimumTestDays;

        var trainingRows = trainingDays.SelectMany(x => x.Rows).OrderBy(x => x.TimestampUtc).ToList();
        var calibrationRows = calibrationDays.SelectMany(x => x.Rows).OrderBy(x => x.TimestampUtc).ToList();
        var testRows = testDays.SelectMany(x => x.Rows).OrderBy(x => x.TimestampUtc).ToList();
        var identity = BuildIdentity(settings, selected.CohortKey, configurationIdentity);
        var cappedTrainingRows = CapDeterministically(trainingRows, settings.MaximumTrainingSamples);
        var candidates = BuildCandidates(cappedTrainingRows, calibrationRows, testRows,
            identity, settings);

        var comparatorCandidates = candidates.Where(x => x.EligibleForRanking &&
            x.Fit.IsUsable && x.TestPredictions.Count > 0).ToList();
        var fairKeys = comparatorCandidates.Count == 0
            ? new HashSet<long>()
            : comparatorCandidates.Select(x => x.TestPredictions.Keys.ToHashSet())
                .Aggregate((left, right) =>
                {
                    left.IntersectWith(right);
                    return left;
                });
        var fairPoints = BuildFairPoints(testRows, fairKeys);
        var modelReports = candidates.Select(x => ToModelReport(x, fairPoints, trainingDays,
            calibrationDays, testDays)).ToList();
        foreach (var diagnostic in modelReports.Where(x => x.LoadProxy ==
            DifferentialLoadProxy.CONNECTOR_POWER.WireName() && !x.EligibleForRanking))
            warnings.Add($"{diagnostic.Name} is diagnostic only: {diagnostic.EligibilityDetail}");
        int fairCount = fairPoints.Count;

        string? winner = null;
        if (!input.Truncated && enoughDays && comparatorCandidates.Count >= 2 &&
            fairCount >= settings.MinimumEvaluationRows)
        {
            winner = modelReports.Where(x => x.EligibleForRanking && x.IsAvailable &&
                    x.FairMetrics is not null)
                .OrderBy(x => x.FairMetrics!.MeanAbsoluteErrorV)
                .ThenBy(x => x.FairMetrics!.RootMeanSquareErrorV)
                .ThenBy(x => x.Name, StringComparer.Ordinal)
                .Select(x => x.Name).FirstOrDefault();
        }

        var state = input.Truncated
            ? ShadowEvaluationState.TRUNCATED_INPUT
            : enoughDays && fairCount >= settings.MinimumEvaluationRows && winner is not null
                ? ShadowEvaluationState.SUFFICIENT
                : ShadowEvaluationState.INSUFFICIENT_DATA;
        if (!enoughDays)
            warnings.Add($"At least {settings.MinimumTrainingDays} training, {settings.MinimumCalibrationDays} calibration, and {settings.MinimumTestDays} test days with independent load coverage are required.");
        if (fairCount < settings.MinimumEvaluationRows)
            warnings.Add($"Only {fairCount} rows are shared by the eligible comparison set on heldout data; {settings.MinimumEvaluationRows} are required for a fair winner.");
        if (comparatorCandidates.Count < 2)
            warnings.Add("Fewer than two eligible, available candidates remain in the comparison set; diagnostic-only or unavailable models do not block their individual metrics.");
        if (winner is null)
            warnings.Add("No winner is declared because the comparison is insufficient, truncated, or unavailable.");
        else
        {
            var ranked = modelReports.Where(x => x.EligibleForRanking && x.IsAvailable &&
                    x.FairMetrics is not null)
                .OrderBy(x => x.FairMetrics!.MeanAbsoluteErrorV)
                .ToList();
            if (ranked.Count > 1 &&
                ranked[1].FairMetrics!.MeanAbsoluteErrorV - ranked[0].FairMetrics!.MeanAbsoluteErrorV <
                    Math.Max(.005, ranked[1].FairMetrics!.MeanAbsoluteErrorV * .05))
                warnings.Add("The best heldout error is only marginally better than the next candidate; this descriptive comparison has no independent ground-truth failure labels and does not establish a meaningful improvement.");
        }

        var coverage = new ShadowCoverage(selected.CohortKey, selected.Rows.Count,
            selected.SupportedDays.Count, trainingDays.Count, calibrationDays.Count,
            testDays.Count, trainingRows.Count, calibrationRows.Count, testRows.Count,
            fairCount, trainingDays.Select(x => x.DayKey).ToList(),
            calibrationDays.Select(x => x.DayKey).ToList(), testDays.Select(x => x.DayKey).ToList());
        var advisory = BuildAdvisory(candidates, testRows, calibrationRows,
            settings, identity, selected.CohortKey,
            trainingDays.Select(x => x.DayKey).ToList(),
            calibrationDays.Select(x => x.DayKey).ToList(), configurationIdentity,
            fingerprint);
        var pcie = settings.IncludePcieCandidate
            ? BuildPcieCandidate(trainingRows, calibrationRows, testRows, settings)
            : null;
        var conclusion = winner is null
            ? "Prediction accuracy comparison is inconclusive for this frozen chronological split; it is not evidence of connector health or failure."
            : $"{winner} has the lowest fair heldout prediction error for this frozen chronological split. This describes prediction accuracy only and is not proof of connector health or failure.";

        return new ShadowEvaluationReport(state, winner, conclusion, cutoff, fingerprint,
            modelIdentity, configurationIdentity, coverage, allPartitions,
            modelReports, advisory, pcie, warnings, BuildInventory(input),
            comparatorCandidates.Select(x => x.Definition.Name).ToList());
    }

    static ShadowEvaluationReport EmptyReport(ShadowEvaluationState state,
        DateTimeOffset cutoff, string fingerprint, ShadowEvaluationOptions settings,
        IReadOnlyList<ShadowDayPartition> partitions, IReadOnlyList<string> warnings,
        string detail, ShadowInputInventory inventory, string modelIdentity,
        string configurationIdentity)
    {
        var coverage = new ShadowCoverage("none", 0, 0, 0, 0, 0, 0, 0, 0, 0,
            Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>());
        var advisory = new ShadowAdvisoryEvaluation(false, "none", null, 0, 0, null, 0,
            null, "UNKNOWN", false, Array.Empty<ShadowAdvisoryTransition>(),
            Array.Empty<ShadowSyntheticFaultEvaluation>(), detail);
        var allWarnings = warnings.Concat(new[] { detail }).ToList();
        return new ShadowEvaluationReport(state, null,
            "Prediction accuracy comparison is inconclusive because the frozen chronological split is unavailable; it is not evidence of connector health or failure.",
            cutoff, fingerprint, modelIdentity, configurationIdentity,
            coverage, partitions, Array.Empty<ShadowModelEvaluation>(), advisory, null,
            allWarnings, inventory);
    }

    static ShadowInputInventory BuildInventory(ShadowReadResult input) =>
        new(input.RowsRead, input.EligibleRawRows, input.InvalidRows, input.RejectedRows,
            input.DuplicateRows, input.ConflictingRows, input.PartialRows, input.Truncated,
            input.Files ?? Array.Empty<ShadowSourceFile>());

    static List<PreparedRow> PrepareRows(IReadOnlyList<ShadowTelemetryRow> rows,
        DateTimeOffset cutoff, ShadowEvaluationOptions settings, List<string> warnings)
    {
        var accepted = new List<PreparedRow>();
        int invalid = 0;
        int outOfRangeVoltage = 0;
        foreach (var row in rows ?? Array.Empty<ShadowTelemetryRow>())
        {
            if (row is null || row.TimestampUtc == default ||
                !double.IsFinite(row.VoltageV) ||
                row.ObservationCount < settings.MinimumObservationsPerMinute)
            {
                invalid++;
                continue;
            }
            if (row.VoltageV < settings.MinimumSourceVoltageV ||
                row.VoltageV > settings.MaximumSourceVoltageV)
            {
                outOfRangeVoltage++;
                continue;
            }
            var timestamp = row.TimestampUtc.ToUniversalTime();
            // A day is eligible only once the entire UTC day is before the
            // reader cutoff date.  Current-day rows can never leak into test.
            if (timestamp.Date >= cutoff.Date) continue;
            string cohort = string.IsNullOrWhiteSpace(row.CohortKey)
                ? "unspecified" : row.CohortKey.Trim();
            var minute = new DateTimeOffset(timestamp.Year, timestamp.Month, timestamp.Day,
                timestamp.Hour, timestamp.Minute, 0, TimeSpan.Zero);
            accepted.Add(new PreparedRow(row, timestamp, minute, cohort,
                timestamp.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)));
        }
        if (invalid > 0) warnings.Add($"{invalid} source rows were excluded before minute qualification.");
        if (outOfRangeVoltage > 0)
            warnings.Add($"{outOfRangeVoltage} rows were excluded because connector voltage was outside the source plausibility range {settings.MinimumSourceVoltageV:R}..{settings.MaximumSourceVoltageV:R} V.");

        // A source may contain repeated raw observations for one minute.  A
        // deterministic best-row choice preserves one minute per cohort and
        // prevents repeated samples from weighting a day or a fit.
        return accepted.GroupBy(x => (x.CohortKey, x.MinuteUtc))
            .Select(group => group.OrderByDescending(x => FeatureCount(x.Row))
                .ThenByDescending(x => x.Row.ObservationCount)
                .ThenBy(x => x.TimestampUtc)
                .ThenBy(x => x.Row.VoltageV)
                .First())
            .OrderBy(x => x.CohortKey, StringComparer.Ordinal)
            .ThenBy(x => x.TimestampUtc)
            .ToList();

        static int FeatureCount(ShadowTelemetryRow row) =>
            new[] { row.ConnectorCurrentA, row.ConnectorPowerW, row.BoardPowerW,
                row.TemperatureC, row.PcieVoltageV }
            .Count(x => x is double value && double.IsFinite(value));
    }

    static List<DayBucket> BuildBuckets(IReadOnlyList<PreparedRow> rows,
        ShadowEvaluationOptions settings) => rows
        .GroupBy(x => (x.CohortKey, x.DayKey))
        .Select(group =>
        {
            var groupRows = group.OrderBy(x => x.TimestampUtc).ToList();
            var power = groupRows.Where(x => Finite(x.Row.ConnectorPowerW)).ToList();
            var current = groupRows.Where(x => Finite(x.Row.ConnectorCurrentA)).ToList();
            var pcie = groupRows.Where(x => Finite(x.Row.PcieVoltageV)).ToList();
            var temperature = groupRows.Where(x => Finite(x.Row.TemperatureC)).ToList();
            double? powerSpan = Span(power.Select(x => x.Row.ConnectorPowerW));
            double? currentSpan = Span(current.Select(x => x.Row.ConnectorCurrentA));
            double? temperatureSpan = Span(temperature.Select(x => x.Row.TemperatureC));
            bool supported = power.Count >= settings.MinimumMinutesPerSupportedDay &&
                current.Count >= settings.MinimumMinutesPerSupportedDay &&
                (powerSpan ?? 0) >= settings.MinimumPowerSpanW &&
                (currentSpan ?? 0) >= settings.MinimumCurrentSpanA;
            string detail = supported
                ? "Independent connector power and current minute coverage meets the shadow requirements."
                : "Day lacks independent minute coverage or load span for both connector load proxies.";
            return new DayBucket(group.Key.CohortKey, group.Key.DayKey, groupRows,
                power.Count, current.Count, pcie.Count, powerSpan, currentSpan,
                temperature.Count, Minimum(temperature.Select(x => x.Row.TemperatureC)),
                Maximum(temperature.Select(x => x.Row.TemperatureC)), temperatureSpan,
                Minimum(power.Select(x => x.Row.ConnectorPowerW)),
                Maximum(power.Select(x => x.Row.ConnectorPowerW)),
                Minimum(current.Select(x => x.Row.ConnectorCurrentA)),
                Maximum(current.Select(x => x.Row.ConnectorCurrentA)),
                supported, detail);
        })
        .OrderBy(x => x.CohortKey, StringComparer.Ordinal)
        .ThenBy(x => x.DayKey, StringComparer.Ordinal)
        .ToList();

    sealed record SelectedCohort(string CohortKey, IReadOnlyList<PreparedRow> Rows,
        IReadOnlyList<DayBucket> SupportedDays);

    static SelectedCohort? SelectCohort(IReadOnlyList<DayBucket> buckets,
        ShadowEvaluationOptions settings)
    {
        var candidates = buckets.GroupBy(x => x.CohortKey, StringComparer.Ordinal)
            .Select(group =>
            {
                var supported = group.Where(x => x.IsSupported)
                    .OrderBy(x => x.DayKey, StringComparer.Ordinal).ToList();
                return new
                {
                    Cohort = group.Key,
                    Rows = group.SelectMany(x => x.Rows).OrderBy(x => x.TimestampUtc).ToList(),
                    Supported = supported,
                };
            })
            .OrderByDescending(x => x.Supported.Count)
            .ThenBy(x => x.Cohort, StringComparer.Ordinal)
            .ToList();
        var selected = candidates.FirstOrDefault();
        if (selected is null || selected.Supported.Count <
            settings.MinimumTrainingDays + settings.MinimumCalibrationDays + settings.MinimumTestDays)
            return null;
        return new SelectedCohort(selected.Cohort, selected.Rows, selected.Supported);
    }

    static ShadowDayPartition ToPublicPartition(DayBucket bucket) => new(bucket.CohortKey,
        bucket.DayKey, bucket.Rows.Count, bucket.PowerMinutes, bucket.CurrentMinutes,
        bucket.PcieMinutes, bucket.PowerSpanW, bucket.CurrentSpanA, bucket.IsSupported,
        bucket.Detail)
    {
        TemperatureValidMinutes = bucket.TemperatureValidMinutes,
        TemperatureMinimumC = bucket.TemperatureMinimumC,
        TemperatureMaximumC = bucket.TemperatureMaximumC,
        TemperatureSpanC = bucket.TemperatureSpanC,
        PowerMinimumW = bucket.PowerMinimumW,
        PowerMaximumW = bucket.PowerMaximumW,
        CurrentMinimumA = bucket.CurrentMinimumA,
        CurrentMaximumA = bucket.CurrentMaximumA,
    };

    static List<CandidateRun> BuildCandidates(
        IReadOnlyList<PreparedRow> trainingRows,
        IReadOnlyList<PreparedRow> calibrationRows,
        IReadOnlyList<PreparedRow> testRows,
        DifferentialModelIdentity identity,
        ShadowEvaluationOptions settings)
    {
        var definitions = new[]
        {
            new CandidateDefinition(ShadowModelKind.POWER_BOARD_TEMPERATURE,
                "POWER_BOARD_TEMPERATURE", DifferentialLoadProxy.CONNECTOR_POWER, true, true),
            new CandidateDefinition(ShadowModelKind.POWER_LOAD_ONLY,
                "POWER_LOAD_ONLY", DifferentialLoadProxy.CONNECTOR_POWER, false, false),
            new CandidateDefinition(ShadowModelKind.CURRENT_BOARD_TEMPERATURE,
                "CURRENT_BOARD_TEMPERATURE", DifferentialLoadProxy.CONNECTOR_CURRENT, true, true),
            new CandidateDefinition(ShadowModelKind.CURRENT_LOAD_ONLY,
                "CURRENT_LOAD_ONLY", DifferentialLoadProxy.CONNECTOR_CURRENT, false, false),
        };
        var result = new List<CandidateRun>(definitions.Length);
        var powerProvenance = AssessPowerProvenance(trainingRows.Concat(calibrationRows)
            .Concat(testRows));
        var advisoryPowerProvenance = AssessPowerProvenance(
            trainingRows.Concat(calibrationRows));
        foreach (var definition in definitions)
        {
            var options = new DifferentialModelOptions
            {
                LoadProxy = definition.Proxy,
                IncludeBoardPower = definition.IncludeBoardPower,
                IncludeTemperature = definition.IncludeTemperature,
                IncludeFanPercent = false,
                IncludeThermalState = false,
                MinimumSamples = 2,
                MinimumSlopeSamples = 2,
                MinimumLoadSpan = definition.Proxy == DifferentialLoadProxy.CONNECTOR_POWER
                    ? settings.MinimumPowerSpanW : settings.MinimumCurrentSpanA,
                Identity = identity,
                ArtifactCreatedAtUtc = trainingRows.Count == 0
                    ? DateTimeOffset.UnixEpoch : trainingRows[^1].TimestampUtc,
            };
            var fitSamples = trainingRows.Select(x => ToSample(x, definition.Proxy,
                identity.CanonicalKey)).ToList();
            var fit = DifferentialModelTrainer.Fit(fitSamples, options);
            var run = new CandidateRun
            {
                Definition = definition,
                Fit = fit,
                TrainingRows = trainingRows.Select(x => x.Row).ToList(),
                CalibrationRows = calibrationRows.Select(x => x.Row).ToList(),
                TestRows = testRows.Select(x => x.Row).ToList(),
            };
            if (definition.Proxy == DifferentialLoadProxy.CONNECTOR_POWER)
            {
                run.TargetCoupledPredictor = powerProvenance.HasDerived;
                run.EligibleForRanking = powerProvenance.Eligible;
                run.EligibleForAdvisory = advisoryPowerProvenance.Eligible;
                run.PowerProvenance = powerProvenance.Label;
                run.EligibilityDetail = powerProvenance.Detail;
            }
            else
            {
                run.EligibleForRanking = true;
                run.EligibleForAdvisory = true;
                run.EligibilityDetail = "Connector current is an independent observed load proxy.";
            }
            foreach (var prepared in calibrationRows)
            {
                var prediction = fit.Artifact.Predict(ToSample(prepared, definition.Proxy,
                    identity.CanonicalKey));
                if (prediction.IsAvailable)
                    run.CalibrationPredictions[prepared.TimestampUtc.UtcTicks] = prediction;
            }
            foreach (var prepared in testRows)
            {
                var prediction = fit.Artifact.Predict(ToSample(prepared, definition.Proxy,
                    identity.CanonicalKey));
                if (prediction.IsAvailable)
                    run.TestPredictions[prepared.TimestampUtc.UtcTicks] = prediction;
                else if (prediction.Qualification.Reasons.Contains(
                    DifferentialSampleRejectionReason.OUTSIDE_ENVELOPE))
                    run.OutOfEnvelopeRows++;
            }
            result.Add(run);
        }
        return result;
    }

    static PowerProvenanceAssessment AssessPowerProvenance(
        IEnumerable<PreparedRow> rows)
    {
        var labels = rows.Where(x => Finite(x.Row.ConnectorPowerW))
            .Select(x => ClassifyPowerProvenance(x.Row.PowerProvenance))
            .ToList();
        bool hasDerived = labels.Any(x => x == "DERIVED_VXI");
        bool hasUnknown = labels.Any(x => x == "UNKNOWN");
        bool hasIndependent = labels.Any(x => x == "INDEPENDENT");
        string label = hasDerived && hasUnknown ? "MIXED_DERIVED_UNKNOWN" :
            hasDerived && hasIndependent ? "MIXED_DERIVED_INDEPENDENT" :
            hasDerived ? "DERIVED_VXI" : hasUnknown && hasIndependent
                ? "MIXED_UNKNOWN_INDEPENDENT" : hasUnknown ? "UNKNOWN" :
            hasIndependent ? "INDEPENDENT" : "UNAVAILABLE";
        bool eligible = labels.Count > 0 && labels.All(x => x == "INDEPENDENT");
        string detail = eligible
            ? "Power provenance is explicitly independently measured; this candidate may enter ranking."
            : hasDerived
                ? "Diagnostic only: connector power is derived from response voltage and current (V*I), so accuracy is target-coupled."
                : "Diagnostic only: connector-power provenance is unknown or mixed and cannot support ranking.";
        return new PowerProvenanceAssessment(label, hasDerived, hasUnknown,
            hasIndependent, eligible, detail);
    }

    static string ClassifyPowerProvenance(string? provenance)
    {
        if (string.IsNullOrWhiteSpace(provenance)) return "UNKNOWN";
        string text = provenance.Trim().ToUpperInvariant();
        if (text.Contains("DERIVED", StringComparison.Ordinal) ||
            text.Contains("V*I", StringComparison.Ordinal) ||
            text.Contains("VXI", StringComparison.Ordinal) ||
            text.Contains("CALCULATED", StringComparison.Ordinal))
            return "DERIVED_VXI";
        if (text.Contains("NATIVE", StringComparison.Ordinal) ||
            text.Contains("MEASURED", StringComparison.Ordinal) ||
            text.Contains("DIRECT", StringComparison.Ordinal) ||
            text.Contains("HARDWARE", StringComparison.Ordinal) ||
            text.Contains("SENSOR", StringComparison.Ordinal))
            return "INDEPENDENT";
        return "UNKNOWN";
    }

    static string ComputeConfigurationIdentity(ShadowEvaluationOptions settings)
    {
        static string Number(double value) => value.ToString("R", CultureInfo.InvariantCulture);
        string[] canonicalSettings =
        [
            $"ModelAlgorithmIdentity={ModelAlgorithmIdentity}",
            $"ModelIdentityLabel={settings.ModelIdentity}",
            $"ConfigurationIdentityLabel={settings.ConfigurationIdentity}",
            $"MinimumTrainingDays={settings.MinimumTrainingDays.ToString(CultureInfo.InvariantCulture)}",
            $"MinimumCalibrationDays={settings.MinimumCalibrationDays.ToString(CultureInfo.InvariantCulture)}",
            $"MinimumTestDays={settings.MinimumTestDays.ToString(CultureInfo.InvariantCulture)}",
            $"MinimumMinutesPerSupportedDay={settings.MinimumMinutesPerSupportedDay.ToString(CultureInfo.InvariantCulture)}",
            $"MinimumPowerSpanW={Number(settings.MinimumPowerSpanW)}",
            $"MinimumCurrentSpanA={Number(settings.MinimumCurrentSpanA)}",
            $"MinimumEvaluationRows={settings.MinimumEvaluationRows.ToString(CultureInfo.InvariantCulture)}",
            $"MinimumObservationsPerMinute={settings.MinimumObservationsPerMinute.ToString(CultureInfo.InvariantCulture)}",
            $"MaximumTrainingSamples={settings.MaximumTrainingSamples.ToString(CultureInfo.InvariantCulture)}",
            $"MinimumPracticalVoltageV={Number(settings.MinimumPracticalVoltageV)}",
            $"MinimumSourceVoltageV={Number(settings.MinimumSourceVoltageV)}",
            $"MaximumSourceVoltageV={Number(settings.MaximumSourceVoltageV)}",
            $"MinimumAdvisoryDropV={Number(settings.MinimumAdvisoryDropV)}",
            $"AdvisoryNoiseMultiplier={Number(settings.AdvisoryNoiseMultiplier)}",
            $"EwmaHalfLifeMinutes={Number(settings.EwmaHalfLifeMinutes)}",
            $"SustainedPersistenceSeconds={Number(settings.SustainedPersistenceSeconds)}",
            $"GapResetMinutes={Number(settings.GapResetMinutes)}",
            $"GradualRampFinalDropV={Number(settings.GradualRampFinalDropV)}",
            $"IncludePcieCandidate={settings.IncludePcieCandidate.ToString(CultureInfo.InvariantCulture)}",
            $"ScenarioOnsetOffsetSeconds={ScenarioOnsetOffsetSeconds.ToString(CultureInfo.InvariantCulture)}",
            $"ScenarioWindowDurationSeconds={ScenarioWindowDurationSeconds.ToString(CultureInfo.InvariantCulture)}",
            $"ScenarioRampDurationSeconds={ScenarioRampDurationSeconds.ToString(CultureInfo.InvariantCulture)}",
        ];
        string canonical = JsonSerializer.Serialize(canonicalSettings);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)))
            .ToLowerInvariant();
    }

    static DifferentialModelIdentity BuildIdentity(ShadowEvaluationOptions settings,
        string cohort, string configurationIdentity) => new(
        gpuUuid: settings.ModelIdentity,
        board: cohort,
        driver: "shadow-offline",
        voltageSource: "shadow-connector-voltage",
        configurationId: configurationIdentity);

    static DifferentialSample ToSample(PreparedRow row, DifferentialLoadProxy proxy,
        string identity) => ToSample(row.Row, row.TimestampUtc, proxy, identity);

    static DifferentialSample ToSample(ShadowTelemetryRow row, DateTimeOffset timestamp,
        DifferentialLoadProxy proxy, string identity) => new(timestamp, row.VoltageV,
        DifferentialFeatureVector.Create(row.ConnectorCurrentA, row.ConnectorPowerW,
            row.BoardPowerW, row.TemperatureC, externalSensorPowerW: null),
        isFresh: true, isSynchronized: true, isSettled: true, ageSeconds: 0,
        voltageTimestampUtc: timestamp, featureTimestampUtc: timestamp,
        identity: identity);

    static IReadOnlyList<PreparedRow> CapDeterministically(IReadOnlyList<PreparedRow> rows,
        int maximum)
    {
        if (rows.Count <= maximum) return rows;
        var result = new List<PreparedRow>(maximum);
        for (int i = 0; i < maximum; i++)
        {
            int index = (int)Math.Floor((double)i * (rows.Count - 1) / (maximum - 1));
            if (result.Count == 0 || result[^1].TimestampUtc != rows[index].TimestampUtc)
                result.Add(rows[index]);
        }
        return result;
    }

    static IReadOnlyList<PreparedRow> BuildFairPoints(
        IReadOnlyList<PreparedRow> testRows, IReadOnlySet<long> fairKeys) =>
        testRows.Where(x => fairKeys.Contains(x.TimestampUtc.UtcTicks)).ToList();

    static ShadowModelEvaluation ToModelReport(CandidateRun run,
        IReadOnlyList<PreparedRow> fairPoints,
        IReadOnlyList<DayBucket> trainingDays,
        IReadOnlyList<DayBucket> calibrationDays,
        IReadOnlyList<DayBucket> testDays)
    {
        var rowsByKey = testDays.SelectMany(x => x.Rows)
            .ToDictionary(x => x.TimestampUtc.UtcTicks);
        var native = run.TestPredictions.Select(pair =>
        {
            rowsByKey.TryGetValue(pair.Key, out var row);
            return row is null || pair.Value.ResidualVolts is not double error
                ? null : new ErrorPoint(row, error);
        }).Where(x => x is not null).Select(x => x!).ToList();
        var fair = fairPoints
            .Select(x => run.TestPredictions.TryGetValue(x.TimestampUtc.UtcTicks,
                out var prediction) && prediction.ResidualVolts is double error &&
                double.IsFinite(error) ? new ErrorPoint(x, error) : null)
            .Where(x => x is not null).Select(x => x!).ToList();
        var fit = run.Fit.Artifact.Diagnostics;
        bool available = run.Fit.IsUsable && run.TestPredictions.Count > 0;
        int nativeRows = run.TestRows.Count(x => HasNativeFeatures(x, run.Definition));
        return new ShadowModelEvaluation(run.Definition.Kind, run.Definition.Name,
            run.Definition.Proxy.WireName(), run.Definition.IncludeBoardPower,
            run.Definition.IncludeTemperature, available,
            fit.State.ToString(), fit.QualifiedSamples, run.CalibrationPredictions.Count,
            nativeRows, run.TestPredictions.Count, run.OutOfEnvelopeRows,
            CalculateMetrics(native), CalculateMetrics(fair),
            trainingDays.Select(x => x.DayKey).ToList(),
            calibrationDays.Select(x => x.DayKey).ToList(),
            testDays.Select(x => x.DayKey).ToList(), fit.Detail)
        {
            TargetCoupledPredictor = run.TargetCoupledPredictor,
            EligibleForRanking = run.EligibleForRanking,
            PowerProvenance = run.PowerProvenance,
            EligibilityDetail = run.EligibilityDetail,
        };

        static bool HasNativeFeatures(ShadowTelemetryRow row, CandidateDefinition definition)
        {
            double? load = definition.Proxy == DifferentialLoadProxy.CONNECTOR_POWER
                ? row.ConnectorPowerW : row.ConnectorCurrentA;
            return Finite(load) && (!definition.IncludeBoardPower || Finite(row.BoardPowerW)) &&
                (!definition.IncludeTemperature || Finite(row.TemperatureC));
        }
    }

    static ShadowErrorMetrics? CalculateMetrics(IReadOnlyList<ErrorPoint> points)
    {
        if (points.Count == 0) return null;
        var errors = points.Select(x => x.ErrorV).Where(double.IsFinite).ToList();
        if (errors.Count == 0) return null;
        double mae = errors.Select(Math.Abs).Average();
        double rmse = Math.Sqrt(errors.Select(x => x * x).Average());
        double bias = errors.Average();
        double tail = Quantile(errors.Select(Math.Abs).OrderBy(x => x).ToArray(), .95);
        var bands = new[]
        {
            ("0-20A", 0d, (double?)20),
            ("20-40A", 20d, (double?)40),
            ("40-60A", 40d, (double?)60),
            ("60A+", 60d, (double?)null),
        };
        var perBand = new List<ShadowCurrentBandError>();
        foreach (var (label, minimum, maximum) in bands)
        {
            var band = points.Where(x => Finite(x.Row.Row.ConnectorCurrentA) &&
                x.Row.Row.ConnectorCurrentA!.Value >= minimum &&
                (maximum is null || x.Row.Row.ConnectorCurrentA!.Value < maximum))
                .Select(x => x.ErrorV).Where(double.IsFinite).ToList();
            if (band.Count == 0) continue;
            perBand.Add(new ShadowCurrentBandError(label, minimum, maximum, band.Count,
                band.Select(Math.Abs).Average(), Math.Sqrt(band.Select(x => x * x).Average()),
                band.Average(), Quantile(band.Select(Math.Abs).OrderBy(x => x).ToArray(), .95)));
        }
        return new ShadowErrorMetrics(errors.Count, mae, rmse, bias, tail, perBand);
    }

    static ShadowAdvisoryEvaluation BuildAdvisory(
        IReadOnlyList<CandidateRun> candidates,
        IReadOnlyList<PreparedRow> testRows,
        IReadOnlyList<PreparedRow> calibrationRows,
        ShadowEvaluationOptions settings,
        DifferentialModelIdentity identity,
        string cohortKey,
        IReadOnlyList<string> trainingDayKeys,
        IReadOnlyList<string> calibrationDayKeys,
        string configurationIdentity,
        string frozenCutoffFingerprint)
    {
        var chosen = candidates.Where(x => x.EligibleForAdvisory && x.Fit.IsUsable &&
                x.CalibrationPredictions.Count >= 2)
            .OrderBy(x => x.Definition.Proxy == DifferentialLoadProxy.CONNECTOR_CURRENT ? 0 : 1)
            .ThenBy(x => x.Definition.IncludeBoardPower && x.Definition.IncludeTemperature ? 0 : 1)
            .ThenBy(x => x.Definition.Name, StringComparer.Ordinal)
            .FirstOrDefault();
        if (chosen is null || !chosen.Fit.IsUsable)
            return new ShadowAdvisoryEvaluation(false, "none", null, 0, 0, null, 0, null,
                "UNKNOWN", false, Array.Empty<ShadowAdvisoryTransition>(),
                Array.Empty<ShadowSyntheticFaultEvaluation>(),
                "No fitted candidate was available for experimental advisory replay.");

        var calibrationErrors = chosen.CalibrationPredictions.Values
            .Select(x => x.ResidualVolts).Where(x => x is double value && double.IsFinite(value))
            .Select(x => x!.Value).ToList();
        if (calibrationErrors.Count < 2)
            return new ShadowAdvisoryEvaluation(false, chosen.Definition.Name, null,
                calibrationErrors.Count, 0, null, 0, null, "UNKNOWN", false,
                Array.Empty<ShadowAdvisoryTransition>(),
                Array.Empty<ShadowSyntheticFaultEvaluation>(),
                "Calibration residual noise is unavailable; experimental advisory is disabled.")
                with
                {
                    CalibrationMedianBiasV = calibrationErrors.Count == 0
                        ? null : Median(calibrationErrors.OrderBy(x => x).ToArray()),
                };
        double calibrationMedian = Median(calibrationErrors.OrderBy(x => x).ToArray());
        double noise = Math.Max(.005,
            1.4826 * Median(calibrationErrors.Select(x => Math.Abs(x - calibrationMedian))
                .OrderBy(x => x).ToArray()));
        var native = ReplayAdvisory(chosen, testRows, settings, identity, noise,
            injectedDrop: null, rampDuration: null, onsetTimestamp: null);
        var synthetic = new List<ShadowSyntheticFaultEvaluation>();
        if (testRows.Count == 0)
        {
            return new ShadowAdvisoryEvaluation(true, chosen.Definition.Name, noise,
                calibrationErrors.Count, native.AvailablePredictions, native.ObservedHours,
                native.Transitions, native.TransitionsPerObservedHour, "UNKNOWN", false,
                native.TransitionList, synthetic,
                "Experimental early advisory replay; live alert thresholds and persistence are unchanged.")
                with { CalibrationMedianBiasV = calibrationMedian };
        }

        DateTimeOffset windowStart = testRows[0].TimestampUtc;
        DateTimeOffset onset = windowStart.AddSeconds(ScenarioOnsetOffsetSeconds);
        DateTimeOffset windowEnd = windowStart.AddSeconds(ScenarioWindowDurationSeconds);
        var windowRows = testRows.Where(x => x.TimestampUtc >= windowStart &&
            x.TimestampUtc < windowEnd).ToList();
        DateTimeOffset? completionEvidence = testRows
            .Where(x => x.TimestampUtc >= windowEnd)
            .Select(x => (DateTimeOffset?)x.TimestampUtc)
            .FirstOrDefault();
        var baselineWindow = ReplayAdvisory(chosen, windowRows, settings, identity, noise,
            injectedDrop: null, rampDuration: null, onsetTimestamp: onset);
        bool windowAvailable = completionEvidence is not null &&
            baselineWindow.AvailablePostOnsetPredictions > 0;
        string noPostOnsetEvidence = baselineWindow.PowerProvenanceRejectedPostOnsetRows > 0
            ? "Connector-power provenance is unknown or derived after onset; replay reset continuity and counted no post-onset evidence."
            : "No baseline post-onset predictions pass model availability and the practical-voltage floor.";
        string unavailableDetail = string.Join(" ", new[]
        {
            completionEvidence is null
                ? "Heldout observations do not reach the exclusive six-hour window end."
                : null,
            baselineWindow.AvailablePostOnsetPredictions == 0
                ? noPostOnsetEvidence
                : null,
        }.Where(x => x is not null));

        foreach (double drop in new[] { .02, .05, .1, .2 })
        {
            synthetic.Add(BuildSyntheticScenario($"STEP_{(int)Math.Round(drop * 1000, MidpointRounding.AwayFromZero)}MV",
                drop, rampDuration: null));
        }
        synthetic.Add(BuildSyntheticScenario("GRADUAL_RAMP",
            settings.GradualRampFinalDropV,
            TimeSpan.FromSeconds(ScenarioRampDurationSeconds)));

        return new ShadowAdvisoryEvaluation(true, chosen.Definition.Name, noise,
            calibrationErrors.Count, native.AvailablePredictions, native.ObservedHours,
            native.Transitions, native.TransitionsPerObservedHour, "UNKNOWN", false,
            native.TransitionList, synthetic,
            "Experimental early advisory replay; live alert thresholds and persistence are unchanged.")
            with
            {
                RowsBelowPracticalVoltageFloor = native.RowsBelowPracticalVoltageFloor,
                GapResets = native.GapResets,
                CalibrationMedianBiasV = calibrationMedian,
            };

        ShadowSyntheticFaultEvaluation BuildSyntheticScenario(string label, double drop,
            TimeSpan? rampDuration)
        {
            string scenarioIdentity = ComputeScenarioIdentity(cohortKey,
                configurationIdentity, frozenCutoffFingerprint,
                trainingDayKeys, calibrationDayKeys,
                chosen.Definition.Name, label, drop, windowStart, onset, windowEnd,
                rampDuration, completionEvidence is not null, windowRows);
            if (!windowAvailable)
            {
                return new ShadowSyntheticFaultEvaluation(label, drop, windowRows.Count,
                    baselineWindow.AvailablePredictions, 0, null, false, unavailableDetail)
                {
                    OnsetTimestampUtc = onset,
                    ScenarioIdentity = scenarioIdentity,
                    ScenarioState = "UNAVAILABLE",
                    WindowStartTimestampUtc = windowStart,
                    WindowEndTimestampUtc = windowEnd,
                    RampDurationSeconds = rampDuration?.TotalSeconds,
                };
            }

            var replay = ReplayAdvisory(chosen, windowRows, settings, identity, noise,
                injectedDrop: drop, rampDuration: rampDuration, onsetTimestamp: onset);
            if (replay.AvailablePostOnsetPredictions == 0)
            {
                string noInjectedEvidence = replay.PowerProvenanceRejectedPostOnsetRows > 0
                    ? "Connector-power provenance is unknown or derived after onset; replay reset continuity and counted no post-onset evidence."
                    : "No injected post-onset predictions pass model availability and the practical-voltage floor.";
                return new ShadowSyntheticFaultEvaluation(label, drop,
                    replay.RowsEvaluated, replay.AvailablePredictions, 0, null, false,
                    noInjectedEvidence)
                {
                    OnsetTimestampUtc = onset,
                    ScenarioIdentity = scenarioIdentity,
                    ScenarioState = "UNAVAILABLE",
                    WindowStartTimestampUtc = windowStart,
                    WindowEndTimestampUtc = windowEnd,
                    RampDurationSeconds = rampDuration?.TotalSeconds,
                };
            }
            var attribution = FindAttributableTransitions(baselineWindow.TransitionList,
                replay.TransitionList, onset);
            return new ShadowSyntheticFaultEvaluation(label, drop, replay.RowsEvaluated,
                replay.AvailablePredictions, replay.Transitions,
                replay.TransitionsPerObservedHour,
                attribution.Count > 0 && replay.AvailablePostOnsetPredictions > 0,
                rampDuration is null
                    ? "Fixed-window connector step began at the fixed onset; training and calibration were untouched."
                    : "Connector drop increased by elapsed wall-clock time and reached the configured final drop after exactly 30 minutes; it then held. Gaps did not change the ramp trajectory.")
            {
                OnsetTimestampUtc = onset,
                FirstAdvisoryTimestampUtc = attribution.FirstAdvisoryUtc,
                DetectionLatencySeconds = DetectionLatency(attribution.FirstAdvisoryUtc, onset),
                AttributableTransitions = attribution.Count,
                ScenarioIdentity = scenarioIdentity,
                ScenarioState = "AVAILABLE",
                WindowStartTimestampUtc = windowStart,
                WindowEndTimestampUtc = windowEnd,
                RampDurationSeconds = rampDuration?.TotalSeconds,
            };
        }
    }

    sealed record AdvisoryReplay(int RowsEvaluated, int AvailablePredictions,
        int Transitions, double? ObservedHours, double? TransitionsPerObservedHour,
        IReadOnlyList<ShadowAdvisoryTransition> TransitionList,
        int RowsBelowPracticalVoltageFloor, int GapResets,
        int AvailablePostOnsetPredictions,
        int PowerProvenanceRejectedPostOnsetRows);

    static string ComputeScenarioIdentity(string cohortKey,
        string configurationIdentity, string frozenCutoffFingerprint,
        IReadOnlyList<string> trainingDayKeys,
        IReadOnlyList<string> calibrationDayKeys, string advisoryModel, string label,
        double finalDropV, DateTimeOffset windowStart, DateTimeOffset onset,
        DateTimeOffset windowEnd, TimeSpan? rampDuration,
        bool windowComplete, IReadOnlyList<PreparedRow> windowRows)
    {
        var payload = new
        {
            ModelIdentity = ModelAlgorithmIdentity,
            ConfigurationIdentity = configurationIdentity,
            FrozenCutoffFingerprint = frozenCutoffFingerprint,
            CohortKey = cohortKey,
            TrainingDayKeys = trainingDayKeys,
            CalibrationDayKeys = calibrationDayKeys,
            AdvisoryModel = advisoryModel,
            Label = label,
            FinalDropV = finalDropV,
            WindowStartUtc = windowStart.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
            OnsetUtc = onset.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
            WindowEndUtcExclusive = windowEnd.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
            RampDurationSeconds = rampDuration?.TotalSeconds,
            WindowComplete = windowComplete,
            SourceObservations = windowRows.Select(x => new
            {
                TimestampUtc = x.TimestampUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
                x.CohortKey,
                x.DayKey,
                x.Row.VoltageV,
                x.Row.ConnectorCurrentA,
                x.Row.ConnectorPowerW,
                x.Row.BoardPowerW,
                x.Row.TemperatureC,
                x.Row.PcieVoltageV,
                x.Row.PowerProvenance,
            }).ToArray(),
        };
        string canonical = JsonSerializer.Serialize(payload);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)))
            .ToLowerInvariant();
    }

    sealed record TransitionAttribution(int Count, DateTimeOffset? FirstAdvisoryUtc);

    static TransitionAttribution FindAttributableTransitions(
        IReadOnlyList<ShadowAdvisoryTransition> baseline,
        IReadOnlyList<ShadowAdvisoryTransition> injected,
        DateTimeOffset? onset)
    {
        if (onset is not DateTimeOffset start) return new(0, null);
        var baselineAfter = baseline.Where(x => x.State == "ADVISORY" &&
            x.TimestampUtc >= start).ToList();
        var unmatched = new bool[baselineAfter.Count];
        var attributable = injected.Where(x => x.State == "ADVISORY" &&
            x.TimestampUtc >= start)
            .Where(x =>
            {
                int match = -1;
                for (int i = 0; i < baselineAfter.Count; i++)
                {
                    if (!unmatched[i] && baselineAfter[i].State == x.State &&
                        baselineAfter[i].TimestampUtc == x.TimestampUtc)
                    {
                        match = i;
                        break;
                    }
                }
                if (match < 0) return true;
                unmatched[match] = true;
                return false;
            }).ToList();
        return new TransitionAttribution(attributable.Count,
            attributable.FirstOrDefault(x => x.State == "ADVISORY")?.TimestampUtc);
    }

    static AdvisoryReplay ReplayAdvisory(CandidateRun candidate,
        IReadOnlyList<PreparedRow> rows, ShadowEvaluationOptions settings,
        DifferentialModelIdentity identity, double noise, double? injectedDrop,
        TimeSpan? rampDuration, DateTimeOffset? onsetTimestamp)
    {
        string state = "UNKNOWN";
        double ewma = 0;
        DateTimeOffset? candidateSince = null;
        DateTimeOffset? previous = null;
        double observedSeconds = 0;
        int observedRows = 0;
        int availablePostOnsetRows = 0;
        int powerProvenanceRejectedPostOnsetRows = 0;
        int belowVoltageFloor = 0;
        int gapResets = 0;
        var transitions = new List<ShadowAdvisoryTransition>();
        for (int index = 0; index < rows.Count; index++)
        {
            var prepared = rows[index];
            if (candidate.Definition.Proxy == DifferentialLoadProxy.CONNECTOR_POWER &&
                ClassifyPowerProvenance(prepared.Row.PowerProvenance) != "INDEPENDENT")
            {
                if (onsetTimestamp is DateTimeOffset rejectedOnset &&
                    prepared.TimestampUtc >= rejectedOnset)
                    powerProvenanceRejectedPostOnsetRows++;
                ewma = 0;
                candidateSince = null;
                previous = null;
                if (state != "UNKNOWN")
                {
                    state = "UNKNOWN";
                    transitions.Add(new ShadowAdvisoryTransition(prepared.TimestampUtc,
                        state, null, "connector-power provenance is unknown or derived; replay continuity reset"));
                }
                continue;
            }
            double drop = 0;
            if (injectedDrop is double finalDrop && onsetTimestamp is DateTimeOffset onset &&
                prepared.TimestampUtc >= onset)
            {
                drop = CalculateSyntheticDropV(finalDrop, prepared.TimestampUtc,
                    onset, rampDuration);
            }
            var sampleRow = drop > 0 ? Inject(prepared.Row, drop) : prepared.Row;
            var prediction = candidate.Fit.Artifact.Predict(ToSample(sampleRow,
                prepared.TimestampUtc, candidate.Definition.Proxy, identity.CanonicalKey));
            double dt = previous is DateTimeOffset prior
                ? (prepared.TimestampUtc - prior).TotalSeconds : 0;
            previous = prepared.TimestampUtc;
            if (!double.IsFinite(dt) || dt < 0) continue;
            if (dt > settings.GapResetMinutes * 60)
            {
                gapResets++;
                ewma = 0;
                candidateSince = null;
                if (state != "UNKNOWN")
                {
                    state = "UNKNOWN";
                    transitions.Add(new ShadowAdvisoryTransition(prepared.TimestampUtc,
                        state, null, "telemetry gap reset"));
                }
                dt = 0;
            }
            else observedSeconds += dt;
            if (!prediction.IsAvailable || sampleRow.VoltageV < settings.MinimumPracticalVoltageV ||
                prediction.ResidualVolts is not double residual || !double.IsFinite(residual))
            {
                if (sampleRow.VoltageV < settings.MinimumPracticalVoltageV)
                    belowVoltageFloor++;
                ewma = 0;
                candidateSince = null;
                previous = null;
                if (state != "UNKNOWN")
                {
                    state = "UNKNOWN";
                    transitions.Add(new ShadowAdvisoryTransition(prepared.TimestampUtc,
                        state, null, "prediction unavailable or below practical voltage floor"));
                }
                continue;
            }
            observedRows++;
            if (onsetTimestamp is DateTimeOffset onsetMoment &&
                prepared.TimestampUtc >= onsetMoment)
                availablePostOnsetRows++;
            double negativeResidual = Math.Max(0, -residual);
            double alpha = dt <= 0 ? 1 : 1 - Math.Exp(-Math.Log(2) * dt /
                (settings.EwmaHalfLifeMinutes * 60));
            ewma = dt <= 0 && observedRows == 1 ? negativeResidual : ewma + alpha * (negativeResidual - ewma);
            double threshold = Math.Max(settings.MinimumAdvisoryDropV,
                noise * settings.AdvisoryNoiseMultiplier);
            if (ewma >= threshold)
                candidateSince ??= prepared.TimestampUtc;
            else
                candidateSince = null;
            bool sustained = candidateSince is DateTimeOffset since &&
                (prepared.TimestampUtc - since).TotalSeconds >= settings.SustainedPersistenceSeconds;
            string next = sustained || state == "ADVISORY" && ewma >= threshold
                ? "ADVISORY" : "UNKNOWN";
            if (!string.Equals(next, state, StringComparison.Ordinal))
            {
                state = next;
                transitions.Add(new ShadowAdvisoryTransition(prepared.TimestampUtc,
                    state, ewma, next == "ADVISORY" ? "sustained residual drop" : "drop below advisory persistence"));
            }
        }
        double? hours = observedSeconds > 0 ? observedSeconds / 3600 : null;
        int advisoryTransitions = transitions.Count(x => x.State == "ADVISORY");
        double? perHour = hours is double h && h > 0 ? advisoryTransitions / h : null;
        return new AdvisoryReplay(rows.Count, observedRows, advisoryTransitions, hours, perHour,
            transitions, belowVoltageFloor, gapResets, availablePostOnsetRows,
            powerProvenanceRejectedPostOnsetRows);
    }

    internal static double CalculateSyntheticDropV(double finalDropV,
        DateTimeOffset timestampUtc, DateTimeOffset onsetUtc, TimeSpan? rampDuration)
    {
        if (timestampUtc < onsetUtc) return 0;
        if (rampDuration is not TimeSpan duration || duration <= TimeSpan.Zero)
            return finalDropV;
        double elapsedSeconds = (timestampUtc - onsetUtc).TotalSeconds;
        return finalDropV * Math.Clamp(elapsedSeconds / duration.TotalSeconds, 0, 1);
    }

    static double? DetectionLatency(DateTimeOffset? firstAdvisory,
        DateTimeOffset? onset) => firstAdvisory is DateTimeOffset first &&
        onset is DateTimeOffset start && first >= start
            ? (first - start).TotalSeconds : null;

    static ShadowTelemetryRow Inject(ShadowTelemetryRow row, double dropV)
    {
        double voltage = Math.Max(.1, row.VoltageV - dropV);
        double? power = row.ConnectorPowerW;
        if (Finite(row.ConnectorCurrentA) && (IsDerivedPower(row.PowerProvenance) ||
            (Finite(power) && Math.Abs(power!.Value - row.VoltageV * row.ConnectorCurrentA!.Value) <=
                Math.Max(.05, Math.Abs(power.Value) * .01))))
            power = voltage * row.ConnectorCurrentA!.Value;
        return row with { VoltageV = voltage, ConnectorPowerW = power };
    }

    static bool IsDerivedPower(string? provenance)
    {
        if (string.IsNullOrWhiteSpace(provenance)) return false;
        var text = provenance.Trim().ToUpperInvariant();
        return text.Contains("DERIVED", StringComparison.Ordinal) ||
            text.Contains("V*I", StringComparison.Ordinal) ||
            text.Contains("VXI", StringComparison.Ordinal) ||
            text.Contains("CALCULATED", StringComparison.Ordinal);
    }

    static ShadowPcieCandidateEvaluation BuildPcieCandidate(
        IReadOnlyList<PreparedRow> trainingRows,
        IReadOnlyList<PreparedRow> calibrationRows,
        IReadOnlyList<PreparedRow> testRows,
        ShadowEvaluationOptions settings)
    {
        const string timingLabel = "UNVERIFIED_NATIVE_RAIL_TIMING";
        var train = trainingRows.Where(x => Finite(x.Row.PcieVoltageV)).ToList();
        if (train.Count < 2 || Span(train.Select(x => x.Row.PcieVoltageV)) is not double span || span <= .001)
            return new ShadowPcieCandidateEvaluation(false, "INSUFFICIENT_DATA", train.Count,
                calibrationRows.Count(x => Finite(x.Row.PcieVoltageV)), testRows.Count,
                0, 0, null, timingLabel,
                "PCIe voltage was unavailable or lacked independent training span.");
        var fit = FitUnivariate(train);
        var points = new List<ErrorPoint>();
        int outOfEnvelope = 0;
        double margin = span * .05;
        foreach (var row in testRows)
        {
            if (!Finite(row.Row.PcieVoltageV)) continue;
            double pcie = row.Row.PcieVoltageV!.Value;
            if (pcie < fit.MinimumPcieV - margin || pcie > fit.MaximumPcieV + margin)
            {
                outOfEnvelope++;
                continue;
            }
            double expected = fit.InterceptV + fit.SlopeVPerV * pcie;
            double error = row.Row.VoltageV - expected;
            if (double.IsFinite(error)) points.Add(new ErrorPoint(row, error));
        }
        var metrics = CalculateMetrics(points);
        return new ShadowPcieCandidateEvaluation(metrics is not null, metrics is null
            ? "UNAVAILABLE" : "AVAILABLE", train.Count,
            calibrationRows.Count(x => Finite(x.Row.PcieVoltageV)), testRows.Count(x => Finite(x.Row.PcieVoltageV)),
            points.Count, outOfEnvelope, metrics, timingLabel,
            metrics is null ? "PCIe candidate had no in-envelope heldout predictions." :
                "Experimental separate PCIe regression; native rail timing remains unverified.");
    }

    static PcieFit FitUnivariate(IReadOnlyList<PreparedRow> rows)
    {
        double intercept = 0;
        double slope = 0;
        for (int iteration = 0; iteration < 4; iteration++)
        {
            var weights = new double[rows.Count];
            for (int i = 0; i < rows.Count; i++)
            {
                double x = rows[i].Row.PcieVoltageV!.Value;
                double error = rows[i].Row.VoltageV - (intercept + slope * x);
                weights[i] = iteration == 0 || Math.Abs(error) <= .05
                    ? 1 : .05 / Math.Abs(error);
            }
            double sumW = weights.Sum();
            double meanX = rows.Select((x, i) => weights[i] * x.Row.PcieVoltageV!.Value).Sum() / sumW;
            double meanY = rows.Select((x, i) => weights[i] * x.Row.VoltageV).Sum() / sumW;
            double denominator = rows.Select((x, i) => weights[i] * Math.Pow(x.Row.PcieVoltageV!.Value - meanX, 2)).Sum();
            slope = denominator <= 1e-12 ? 0 : rows.Select((x, i) => weights[i] *
                (x.Row.PcieVoltageV!.Value - meanX) * (x.Row.VoltageV - meanY)).Sum() / denominator;
            intercept = meanY - slope * meanX;
        }
        return new PcieFit(intercept, slope, rows.Min(x => x.Row.PcieVoltageV!.Value),
            rows.Max(x => x.Row.PcieVoltageV!.Value));
    }

    static string ComputeFingerprint(string cohort,
        IEnumerable<PreparedRow> frozenRows, ShadowEvaluationOptions settings)
    {
        var rowText = frozenRows.OrderBy(x => x.TimestampUtc)
            .Select(x => string.Join(",", new[]
            {
                x.TimestampUtc.ToString("O", CultureInfo.InvariantCulture),
                x.Row.VoltageV.ToString("R", CultureInfo.InvariantCulture),
                Format(x.Row.ConnectorCurrentA), Format(x.Row.ConnectorPowerW),
                Format(x.Row.BoardPowerW), Format(x.Row.TemperatureC),
                Format(x.Row.PcieVoltageV), x.CohortKey, x.Row.PowerProvenance ?? "UNKNOWN",
            }));
        string canonical = string.Join("|", new[]
        {
            cohort, string.Join(";", rowText),
            settings.MinimumTrainingDays.ToString(CultureInfo.InvariantCulture),
            settings.MinimumCalibrationDays.ToString(CultureInfo.InvariantCulture),
            settings.MinimumTestDays.ToString(CultureInfo.InvariantCulture),
            settings.MinimumMinutesPerSupportedDay.ToString(CultureInfo.InvariantCulture),
            settings.MinimumPowerSpanW.ToString("R", CultureInfo.InvariantCulture),
            settings.MinimumCurrentSpanA.ToString("R", CultureInfo.InvariantCulture),
            settings.MaximumTrainingSamples.ToString(CultureInfo.InvariantCulture),
            settings.ModelIdentity, settings.ConfigurationIdentity,
        });
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));

        static string Format(double? value) => value is double number && double.IsFinite(number)
            ? number.ToString("R", CultureInfo.InvariantCulture) : "null";
    }

    static double? Span(IEnumerable<double?> values)
    {
        var finite = values.Where(x => x is double number && double.IsFinite(number))
            .Select(x => x!.Value).ToList();
        if (finite.Count == 0) return null;
        double span = finite.Max() - finite.Min();
        return double.IsFinite(span) ? span : null;
    }

    static double? Minimum(IEnumerable<double?> values)
    {
        var finite = values.Where(x => x is double number && double.IsFinite(number))
            .Select(x => x!.Value).ToList();
        return finite.Count == 0 ? null : finite.Min();
    }

    static double? Maximum(IEnumerable<double?> values)
    {
        var finite = values.Where(x => x is double number && double.IsFinite(number))
            .Select(x => x!.Value).ToList();
        return finite.Count == 0 ? null : finite.Max();
    }

    static bool Finite(double? value) => value is double number && double.IsFinite(number);

    static double Median(IReadOnlyList<double> values)
    {
        if (values.Count == 0) return 0;
        return values.Count % 2 == 1 ? values[values.Count / 2] :
            (values[values.Count / 2 - 1] + values[values.Count / 2]) / 2;
    }

    static double Quantile(IReadOnlyList<double> sorted, double quantile)
    {
        if (sorted.Count == 0) return 0;
        double position = (sorted.Count - 1) * Math.Clamp(quantile, 0, 1);
        int lower = (int)Math.Floor(position);
        int upper = (int)Math.Ceiling(position);
        return sorted[lower] + (sorted[upper] - sorted[lower]) * (position - lower);
    }
}
