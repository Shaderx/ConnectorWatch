using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ConnectorWatch;

/// <summary>
/// Describes the provenance of rows supplied to the offline characterization
/// report.  RECORDED means persisted telemetry was present; it is not a claim
/// that the physical sensor or its wiring has been independently validated.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum CharacterizationEvidence
{
    RECORDED,
    SYNTHETIC,
    NO_DATA,
}

/// <summary>One normalized telemetry row accepted by the characterizer.</summary>
public sealed record RecordedTelemetryRow(
    DateTimeOffset TimestampUtc,
    double? LoadWatts,
    double? VoltageVolts,
    double? TemperatureC,
    string? LoadSource = null,
    bool IsSynthetic = false,
    string? Source = null,
    double? BoardPowerWatts = null,
    double? ConnectorPowerWatts = null,
    double? ConnectorCurrentAmps = null,
    double? PcieVoltageVolts = null,
    double? PcieCurrentAmps = null,
    double? PciePowerWatts = null,
    double? UtilizationPercent = null,
    double? PowerLimitWatts = null,
    double? PollLatencySeconds = null,
    int? ConsecutiveIdenticalObservations = null,
    string? AcquisitionHealth = null);

/// <summary>Controls cadence and gap interpretation.  All values are UTC and
/// seconds; a null expected cadence lets the report use the observed median.
/// </summary>
public sealed record RecordedDataCharacterizationOptions
{
    public double? ExpectedCadenceSeconds { get; init; }
    public double GapMultiplier { get; init; } = 2;
    public double MinimumGapSeconds { get; init; } = 2;

    internal void Validate()
    {
        if (ExpectedCadenceSeconds is double expected &&
            (!double.IsFinite(expected) || expected <= 0))
            throw new ArgumentOutOfRangeException(nameof(ExpectedCadenceSeconds));
        if (!double.IsFinite(GapMultiplier) || GapMultiplier < 1)
            throw new ArgumentOutOfRangeException(nameof(GapMultiplier));
        if (!double.IsFinite(MinimumGapSeconds) || MinimumGapSeconds < 0)
            throw new ArgumentOutOfRangeException(nameof(MinimumGapSeconds));
    }
}

public sealed record CharacterizationRange(
    [property: JsonPropertyName("count")] int Count,
    [property: JsonPropertyName("min")] double? Minimum,
    [property: JsonPropertyName("max")] double? Maximum,
    [property: JsonPropertyName("mean")] double? Mean);

public sealed record CharacterizationTimeRange(
    [property: JsonPropertyName("start_utc")] DateTimeOffset? StartUtc,
    [property: JsonPropertyName("end_utc")] DateTimeOffset? EndUtc,
    [property: JsonPropertyName("duration_seconds")] double? DurationSeconds);

public sealed record CharacterizationRowCounts(
    [property: JsonPropertyName("rows_read")] int RowsRead,
    [property: JsonPropertyName("rows_with_valid_timestamp")] int RowsWithValidTimestamp,
    [property: JsonPropertyName("invalid_rows")] int InvalidRows,
    [property: JsonPropertyName("rows_with_load")] int RowsWithLoad,
    [property: JsonPropertyName("rows_with_voltage")] int RowsWithVoltage,
    [property: JsonPropertyName("rows_with_temperature")] int RowsWithTemperature);

public sealed record CharacterizationCadence(
    [property: JsonPropertyName("distinct_timestamp_count")] int DistinctTimestampCount,
    [property: JsonPropertyName("interval_count")] int IntervalCount,
    [property: JsonPropertyName("min_seconds")] double? MinimumSeconds,
    [property: JsonPropertyName("median_seconds")] double? MedianSeconds,
    [property: JsonPropertyName("mean_seconds")] double? MeanSeconds,
    [property: JsonPropertyName("p95_seconds")] double? P95Seconds,
    [property: JsonPropertyName("max_seconds")] double? MaximumSeconds,
    [property: JsonPropertyName("nominal_seconds")] double? NominalSeconds,
    [property: JsonPropertyName("jitter_p95_seconds")] double? JitterP95Seconds);

public sealed record CharacterizationDuplicates(
    [property: JsonPropertyName("timestamp_row_count")] int TimestampRowCount,
    [property: JsonPropertyName("unique_timestamp_count")] int UniqueTimestampCount,
    [property: JsonPropertyName("duplicate_row_count")] int DuplicateRowCount,
    [property: JsonPropertyName("duplicate_group_count")] int DuplicateGroupCount,
    [property: JsonPropertyName("out_of_order_row_count")] int OutOfOrderRowCount);

public sealed record CharacterizationGaps(
    [property: JsonPropertyName("gap_count")] int GapCount,
    [property: JsonPropertyName("gap_threshold_seconds")] double? GapThresholdSeconds,
    [property: JsonPropertyName("total_gap_seconds")] double TotalGapSeconds,
    [property: JsonPropertyName("missing_seconds")] double MissingSeconds,
    [property: JsonPropertyName("longest_gap_seconds")] double? LongestGapSeconds);

public sealed record CharacterizationSensorAvailability(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("available_rows")] int AvailableRows,
    [property: JsonPropertyName("missing_rows")] int MissingRows,
    [property: JsonPropertyName("availability_percent")] double? AvailabilityPercent);

public sealed record CharacterizationSensorSummary(
    [property: JsonPropertyName("row_count")] int RowCount,
    [property: JsonPropertyName("sensors")] IReadOnlyList<CharacterizationSensorAvailability> Sensors);

public sealed record CharacterizationLoadVoltageAssociation(
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("paired_row_count")] int PairedRowCount,
    [property: JsonPropertyName("mean_load_w")] double? MeanLoadWatts,
    [property: JsonPropertyName("mean_voltage_v")] double? MeanVoltageVolts,
    [property: JsonPropertyName("pearson_r")] double? PearsonCorrelation,
    [property: JsonPropertyName("slope_v_per_w")] double? SlopeVoltsPerWatt,
    [property: JsonPropertyName("intercept_v")] double? InterceptVolts,
    [property: JsonPropertyName("direction")] string Direction);

public sealed record CharacterizationThermalCoverage(
    [property: JsonPropertyName("available_rows")] int AvailableRows,
    [property: JsonPropertyName("missing_rows")] int MissingRows,
    [property: JsonPropertyName("coverage_percent")] double? CoveragePercent,
    [property: JsonPropertyName("temperature_range_c")] CharacterizationRange TemperatureRangeC,
    [property: JsonPropertyName("observed_duration_seconds")] double? ObservedDurationSeconds,
    [property: JsonPropertyName("covered_duration_seconds")] double? CoveredDurationSeconds,
    [property: JsonPropertyName("duration_coverage_percent")] double? DurationCoveragePercent);

public sealed record CharacterizationDistribution(
    [property: JsonPropertyName("count")] int Count,
    [property: JsonPropertyName("mean")] double? Mean,
    [property: JsonPropertyName("variance")] double? Variance,
    [property: JsonPropertyName("standard_deviation")] double? StandardDeviation,
    [property: JsonPropertyName("p05")] double? P05,
    [property: JsonPropertyName("median")] double? Median,
    [property: JsonPropertyName("p95")] double? P95);

public sealed record CharacterizationSignalBehavior(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("distribution")] CharacterizationDistribution Distribution,
    [property: JsonPropertyName("unique_value_count")] int UniqueValueCount,
    [property: JsonPropertyName("repeated_update_count")] int RepeatedUpdateCount,
    [property: JsonPropertyName("minimum_observed_step")] double? MinimumObservedStep);

public sealed record CharacterizationCurrentBand(
    [property: JsonPropertyName("minimum_amps")] double MinimumAmps,
    [property: JsonPropertyName("maximum_amps")] double MaximumAmps,
    [property: JsonPropertyName("row_count")] int RowCount,
    [property: JsonPropertyName("voltage_variance")] double? VoltageVariance,
    [property: JsonPropertyName("residual_variance")] double? ResidualVariance);

public sealed record CharacterizationCrossRailTiming(
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("row_aligned_pair_count")] int RowAlignedPairCount,
    [property: JsonPropertyName("unpaired_row_count")] int UnpairedRowCount,
    [property: JsonPropertyName("detail")] string Detail);

public sealed record CharacterizationDrift(
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("window_seconds")] double WindowSeconds,
    [property: JsonPropertyName("first_window_mean_v")] double? FirstWindowMeanVolts,
    [property: JsonPropertyName("last_window_mean_v")] double? LastWindowMeanVolts,
    [property: JsonPropertyName("change_v")] double? ChangeVolts);

public sealed record CharacterizationPollingEvidence(
    [property: JsonPropertyName("unique_voltage_updates")] int UniqueVoltageUpdates,
    [property: JsonPropertyName("repeated_voltage_rows")] int RepeatedVoltageRows,
    [property: JsonPropertyName("poll_latency_seconds")] CharacterizationDistribution PollLatencySeconds,
    [property: JsonPropertyName("reported_identical_observation_maximum")] int? ReportedIdenticalObservationMaximum,
    [property: JsonPropertyName("unhealthy_acquisition_rows")] int UnhealthyAcquisitionRows,
    [property: JsonPropertyName("call_latency_status")] string CallLatencyStatus,
    [property: JsonPropertyName("cpu_status")] string CpuStatus,
    [property: JsonPropertyName("handle_thread_status")] string HandleThreadStatus,
    [property: JsonPropertyName("driver_stability_status")] string DriverStabilityStatus);

public sealed record CharacterizationThresholdProbe(
    [property: JsonPropertyName("injected_droop_mv")] int InjectedDroopMillivolts,
    [property: JsonPropertyName("fast_detected")] bool FastDetected,
    [property: JsonPropertyName("fast_latency_seconds")] double? FastLatencySeconds,
    [property: JsonPropertyName("ewma_detected")] bool EwmaDetected,
    [property: JsonPropertyName("ewma_latency_seconds")] double? EwmaLatencySeconds);

public sealed record CharacterizationDetectorEvaluation(
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("observed_duration_hours")] double? ObservedDurationHours,
    [property: JsonPropertyName("normal_data_alert_transitions")] int NormalDataAlertTransitions,
    [property: JsonPropertyName("false_alerts_per_observed_hour")] double? FalseAlertsPerObservedHour,
    [property: JsonPropertyName("production_fast_threshold_v")] double ProductionFastThresholdVolts,
    [property: JsonPropertyName("production_ewma_threshold_v")] double ProductionEwmaThresholdVolts,
    [property: JsonPropertyName("production_defaults_changed")] bool ProductionDefaultsChanged,
    [property: JsonPropertyName("synthetic_sensitivity_probes")] IReadOnlyList<CharacterizationThresholdProbe> SyntheticSensitivityProbes);

/// <summary>Stable, JSON-ready characterization output.</summary>
public sealed record RecordedDataCharacterizationReport(
    [property: JsonPropertyName("schema_version")] int SchemaVersion,
    [property: JsonPropertyName("evidence")] CharacterizationEvidence Evidence,
    [property: JsonPropertyName("evidence_note")] string EvidenceNote,
    [property: JsonPropertyName("source_files")] IReadOnlyList<string> SourceFiles,
    [property: JsonPropertyName("rows")] CharacterizationRowCounts Rows,
    [property: JsonPropertyName("time_range")] CharacterizationTimeRange TimeRange,
    [property: JsonPropertyName("cadence")] CharacterizationCadence Cadence,
    [property: JsonPropertyName("duplicates")] CharacterizationDuplicates Duplicates,
    [property: JsonPropertyName("gaps")] CharacterizationGaps Gaps,
    [property: JsonPropertyName("sensor_availability")] CharacterizationSensorSummary SensorAvailability,
    [property: JsonPropertyName("load_range_w")] CharacterizationRange LoadRangeWatts,
    [property: JsonPropertyName("voltage_range_v")] CharacterizationRange VoltageRangeVolts,
    [property: JsonPropertyName("load_voltage_association")] CharacterizationLoadVoltageAssociation LoadVoltageAssociation,
    [property: JsonPropertyName("thermal_coverage")] CharacterizationThermalCoverage ThermalCoverage,
    [property: JsonPropertyName("signal_behavior")] IReadOnlyList<CharacterizationSignalBehavior> SignalBehavior,
    [property: JsonPropertyName("current_range_variance")] IReadOnlyList<CharacterizationCurrentBand> CurrentRangeVariance,
    [property: JsonPropertyName("cross_rail_timing")] CharacterizationCrossRailTiming CrossRailTiming,
    [property: JsonPropertyName("warmup_hour_scale_drift")] CharacterizationDrift WarmupHourScaleDrift,
    [property: JsonPropertyName("polling_evidence")] CharacterizationPollingEvidence PollingEvidence,
    [property: JsonPropertyName("detector_evaluation")] CharacterizationDetectorEvaluation DetectorEvaluation,
    [property: JsonPropertyName("workload_types")] IReadOnlyList<string> WorkloadTypes,
    [property: JsonPropertyName("unresolved_assumptions_and_blind_spots")] IReadOnlyList<string> UnresolvedAssumptionsAndBlindSpots)
{
    static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = false,
        PropertyNamingPolicy = null,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>Serializes with fixed property order, invariant numbers and
    /// enum names.  It does not include current time or machine paths.</summary>
    public string ToJson() => JsonSerializer.Serialize(this, Json);
    public string ToDeterministicJson() => ToJson();
}

/// <summary>
/// Reads ConnectorWatch telemetry CSV, rail JSON/JSONL and small status-style
/// JSON records, then computes descriptive offline characteristics.  It never
/// opens a driver or treats aggregate voltage as a safety certification.
/// </summary>
public static class RecordedDataCharacterization
{
    const int SchemaVersion = 2;
    static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    public static RecordedDataCharacterizationReport Analyze(
        IEnumerable<RecordedTelemetryRow> rows,
        RecordedDataCharacterizationOptions? options = null,
        CharacterizationEvidence? evidence = null)
    {
        if (rows is null) throw new ArgumentNullException(nameof(rows));
        var list = rows.ToList();
        var valid = list.Where(IsFiniteTimestamp).ToList();
        return Build(valid, list.Count, list.Count - valid.Count, Array.Empty<string>(),
            list.Any(x => x.IsSynthetic || IsSyntheticText(x.Source ?? "") || IsSyntheticText(x.LoadSource ?? "")),
            evidence, options);
    }

    public static RecordedDataCharacterizationReport AnalyzeFile(
        string path,
        RecordedDataCharacterizationOptions? options = null,
        CharacterizationEvidence? evidence = null)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("A telemetry path is required.", nameof(path));
        if (!File.Exists(path)) throw new FileNotFoundException("Telemetry input was not found.", path);
        var parsed = ParseFile(path);
        var names = new[] { Path.GetFileName(path) };
        return Build(parsed.Rows, parsed.RowsRead, parsed.InvalidRows, names,
            parsed.SyntheticMarker, evidence, options);
    }

    public static RecordedDataCharacterizationReport AnalyzeFiles(
        IEnumerable<string> paths,
        RecordedDataCharacterizationOptions? options = null,
        CharacterizationEvidence? evidence = null)
    {
        if (paths is null) throw new ArgumentNullException(nameof(paths));
        var allRows = new List<RecordedTelemetryRow>();
        var sourceNames = new List<string>();
        int read = 0, invalid = 0;
        bool synthetic = false;
        foreach (var path in paths.Where(p => !string.IsNullOrWhiteSpace(p))
                     .OrderBy(p => p, StringComparer.Ordinal))
        {
            if (!File.Exists(path)) throw new FileNotFoundException("Telemetry input was not found.", path);
            var parsed = ParseFile(path);
            allRows.AddRange(parsed.Rows);
            read += parsed.RowsRead;
            invalid += parsed.InvalidRows;
            synthetic |= parsed.SyntheticMarker;
            sourceNames.Add(Path.GetFileName(path));
        }
        return Build(allRows, read, invalid,
            sourceNames.Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(x => x, StringComparer.Ordinal).ToArray(),
            synthetic, evidence, options);
    }

    /// <summary>Finds likely persisted telemetry under a directory.  Names
    /// are sorted before reading so the report is repeatable.</summary>
    public static IReadOnlyList<string> FindTelemetryFiles(string root)
    {
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)) return Array.Empty<string>();
        return Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Where(IsTelemetryFile)
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToArray();
    }

    public static RecordedDataCharacterizationReport AnalyzeDirectory(
        string root,
        RecordedDataCharacterizationOptions? options = null,
        CharacterizationEvidence? evidence = null) =>
        AnalyzeFiles(FindTelemetryFiles(root), options, evidence);

    static bool IsTelemetryFile(string path)
    {
        var name = Path.GetFileName(path);
        var extension = Path.GetExtension(path);
        if (name.Contains("telemetry", StringComparison.OrdinalIgnoreCase) &&
            extension.Equals(".csv", StringComparison.OrdinalIgnoreCase)) return true;
        if (extension.Equals(".jsonl", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".ndjson", StringComparison.OrdinalIgnoreCase)) return true;
        return extension.Equals(".json", StringComparison.OrdinalIgnoreCase) &&
            (name.Contains("telemetry", StringComparison.OrdinalIgnoreCase) ||
             name.Equals("status.json", StringComparison.OrdinalIgnoreCase));
    }

    static RecordedDataCharacterizationReport Build(
        IReadOnlyList<RecordedTelemetryRow> rows,
        int rowsRead,
        int invalidRows,
        IReadOnlyList<string> sourceFiles,
        bool syntheticMarker,
        CharacterizationEvidence? evidenceHint,
        RecordedDataCharacterizationOptions? suppliedOptions)
    {
        var options = suppliedOptions ?? new RecordedDataCharacterizationOptions();
        options.Validate();
        var ordered = rows.OrderBy(x => x.TimestampUtc).ToList();
        var timestamps = ordered.Select(x => x.TimestampUtc).ToArray();
        var distinct = timestamps.Distinct().OrderBy(x => x).ToArray();
        var loadValues = ordered.Select(x => x.LoadWatts).Where(IsFinite).Select(x => x!.Value).ToArray();
        var voltageValues = ordered.Select(x => x.VoltageVolts).Where(IsFinite).Select(x => x!.Value).ToArray();
        var temperatureValues = ordered.Select(x => x.TemperatureC).Where(IsFinite).Select(x => x!.Value).ToArray();
        var intervals = PositiveIntervals(distinct);
        var nominal = options.ExpectedCadenceSeconds ?? Median(intervals);
        double? threshold = nominal is double cadence
            ? Math.Max(options.MinimumGapSeconds, cadence * options.GapMultiplier)
            : null;
        var gapIntervals = threshold is double gapThreshold
            ? intervals.Where(x => x > gapThreshold).ToArray()
            : Array.Empty<double>();
        var missing = nominal is double expected
            ? intervals.Sum(x => Math.Max(0, x - expected))
            : 0;
        var jitter = nominal is double basis
            ? Percentile(intervals.Select(x => Math.Abs(x - basis)).ToArray(), .95)
            : null;
        var duplicateGroups = timestamps.GroupBy(x => x).Where(g => g.Count() > 1).ToArray();
        var evidence = rows.Count == 0 ? CharacterizationEvidence.NO_DATA :
            evidenceHint is CharacterizationEvidence supplied && supplied != CharacterizationEvidence.NO_DATA
                ? supplied : syntheticMarker ? CharacterizationEvidence.SYNTHETIC : CharacterizationEvidence.RECORDED;
        var sensorValues = BuildSensorValues(ordered);
        var sensorAvailability = sensorValues.Select(x => new CharacterizationSensorAvailability(
            x.Name, x.Values.Count(IsFinite), ordered.Count - x.Values.Count(IsFinite),
            Availability(x.Values.Count(IsFinite), ordered.Count))).ToArray();
        var thermal = Thermal(ordered, temperatureValues, threshold);
        var association = Associate(ordered);
        var residuals = LinearResiduals(ordered, association);
        var signalBehavior = SignalBehavior(ordered, residuals);
        var currentBands = CurrentBands(ordered, residuals);
        var detectorEvaluation = EvaluateDetectors(ordered, residuals);
        return new(
            SchemaVersion,
            evidence,
            EvidenceNote(evidence),
            sourceFiles.ToArray(),
            new CharacterizationRowCounts(rowsRead, rows.Count, invalidRows,
                loadValues.Length, voltageValues.Length, temperatureValues.Length),
            TimeRange(timestamps),
            new CharacterizationCadence(distinct.Length, intervals.Length,
                intervals.Length == 0 ? null : intervals.Min(), Median(intervals),
                Mean(intervals), Percentile(intervals, .95),
                intervals.Length == 0 ? null : intervals.Max(), nominal, jitter),
            new CharacterizationDuplicates(timestamps.Length, distinct.Length,
                timestamps.Length - distinct.Length, duplicateGroups.Length,
                OutOfOrderCount(rows)),
            new CharacterizationGaps(gapIntervals.Length, threshold,
                gapIntervals.Sum(), missing,
                gapIntervals.Length == 0 ? null : gapIntervals.Max()),
            new CharacterizationSensorSummary(ordered.Count, sensorAvailability),
            Range(loadValues), Range(voltageValues),
            association, thermal,
            signalBehavior,
            currentBands,
            CrossRailTiming(ordered),
            Drift(ordered),
            Polling(ordered),
            detectorEvaluation,
            new[] { "UNLABELLED_RECORDED_OPERATION" },
            new[]
            {
                "Native source timestamps are unavailable in these files; row alignment is not proof of cross-rail synchronization.",
                "Per-call latency, CPU, handle/thread counts, and driver health were not recorded; dependent stability claims remain unresolved.",
                "The descriptive linear residual is not the frozen production robust-model residual and is not a resistance measurement.",
                "Synthetic millivolt injections are sensitivity probes only; they are not safety thresholds or physical connector tests.",
                "One-hertz aggregate observations can miss fast transients and cannot establish per-pin current or contact temperature."
            });
    }

    static string EvidenceNote(CharacterizationEvidence evidence) => evidence switch
    {
        CharacterizationEvidence.RECORDED => "Persisted telemetry rows; physical sensor provenance and safety are not established.",
        CharacterizationEvidence.SYNTHETIC => "Synthetic or explicitly marked fixture rows; no physical evidence is claimed.",
        _ => "No valid telemetry rows were supplied; no evidence is available.",
    };

    static CharacterizationTimeRange TimeRange(IReadOnlyList<DateTimeOffset> timestamps)
    {
        if (timestamps.Count == 0) return new(null, null, null);
        var start = timestamps.Min(); var end = timestamps.Max();
        return new(start, end, (end - start).TotalSeconds);
    }

    static CharacterizationRange Range(IEnumerable<double> values)
    {
        var list = values.Where(double.IsFinite).ToArray();
        return list.Length == 0 ? new(0, null, null, null) :
            new(list.Length, list.Min(), list.Max(), list.Average());
    }

    static double? Availability(int available, int total) =>
        total == 0 ? null : available * 100.0 / total;

    static CharacterizationLoadVoltageAssociation Associate(IReadOnlyList<RecordedTelemetryRow> rows)
    {
        var pairs = rows.Where(x => IsFinite(x.LoadWatts) && IsFinite(x.VoltageVolts))
            .Select(x => (Load: x.LoadWatts!.Value, Voltage: x.VoltageVolts!.Value)).ToArray();
        if (pairs.Length < 2)
            return new("NO_DATA", pairs.Length, Mean(pairs.Select(x => x.Load).ToArray()),
                Mean(pairs.Select(x => x.Voltage).ToArray()), null, null, null, "NONE");
        var meanLoad = pairs.Average(x => x.Load);
        var meanVoltage = pairs.Average(x => x.Voltage);
        double xx = 0, yy = 0, xy = 0;
        foreach (var pair in pairs)
        {
            var dx = pair.Load - meanLoad; var dy = pair.Voltage - meanVoltage;
            xx += dx * dx; yy += dy * dy; xy += dx * dy;
        }
        var slope = xx > 0 ? xy / xx : (double?)null;
        double? intercept = slope is double b ? meanVoltage - b * meanLoad : null;
        double? correlation = xx > 0 && yy > 0 ? Math.Clamp(xy / Math.Sqrt(xx * yy), -1, 1) : null;
        var status = xx == 0 ? "CONSTANT_LOAD" : yy == 0 ? "CONSTANT_VOLTAGE" : "AVAILABLE";
        var direction = slope is not double value || Math.Abs(value) < 1e-12 ? "NONE" : value > 0 ? "POSITIVE" : "NEGATIVE";
        return new(status, pairs.Length, meanLoad, meanVoltage, correlation, slope, intercept, direction);
    }

    static CharacterizationThermalCoverage Thermal(
        IReadOnlyList<RecordedTelemetryRow> ordered,
        IReadOnlyList<double> temperatures,
        double? gapThreshold)
    {
        int available = temperatures.Count;
        var unique = ordered.GroupBy(x => x.TimestampUtc).Select(g => g.First()).OrderBy(x => x.TimestampUtc).ToArray();
        double observed = 0, covered = 0;
        for (int i = 1; i < unique.Length; i++)
        {
            var delta = (unique[i].TimestampUtc - unique[i - 1].TimestampUtc).TotalSeconds;
            if (!double.IsFinite(delta) || delta <= 0 || gapThreshold is double gap && delta > gap) continue;
            observed += delta;
            if (IsFinite(unique[i - 1].TemperatureC)) covered += delta;
        }
        return new(available, ordered.Count - available, Availability(available, ordered.Count),
            Range(temperatures), unique.Length > 1 ? observed : null,
            unique.Length > 1 ? covered : null,
            observed > 0 ? covered * 100.0 / observed : null);
    }

    static double?[] LinearResiduals(IReadOnlyList<RecordedTelemetryRow> rows,
        CharacterizationLoadVoltageAssociation association)
    {
        var residuals = new double?[rows.Count];
        if (association.SlopeVoltsPerWatt is not double slope ||
            association.InterceptVolts is not double intercept) return residuals;
        for (int i = 0; i < rows.Count; i++)
            if (IsFinite(rows[i].LoadWatts) && IsFinite(rows[i].VoltageVolts))
                residuals[i] = rows[i].VoltageVolts!.Value -
                    (intercept + slope * rows[i].LoadWatts!.Value);
        return residuals;
    }

    static IReadOnlyList<CharacterizationSignalBehavior> SignalBehavior(
        IReadOnlyList<RecordedTelemetryRow> rows, IReadOnlyList<double?> residuals)
    {
        var signals = new (string Name, double?[] Values)[]
        {
            ("input_voltage_v", rows.Select(x => x.VoltageVolts).ToArray()),
            ("pcie_voltage_v", rows.Select(x => x.PcieVoltageVolts).ToArray()),
            ("connector_minus_pcie_voltage_v", rows.Select(x =>
                IsFinite(x.VoltageVolts) && IsFinite(x.PcieVoltageVolts)
                    ? x.VoltageVolts!.Value - x.PcieVoltageVolts!.Value : (double?)null).ToArray()),
            ("board_power_w", rows.Select(x => x.BoardPowerWatts).ToArray()),
            ("connector_power_w", rows.Select(x => x.ConnectorPowerWatts).ToArray()),
            ("connector_current_a", rows.Select(x => x.ConnectorCurrentAmps).ToArray()),
            ("pcie_current_a", rows.Select(x => x.PcieCurrentAmps).ToArray()),
            ("pcie_power_w", rows.Select(x => x.PciePowerWatts).ToArray()),
            ("descriptive_linear_residual_v", residuals.ToArray()),
        };
        return signals.Select(x => Behavior(x.Name, x.Values)).ToArray();
    }

    static CharacterizationSignalBehavior Behavior(string name, IReadOnlyList<double?> values)
    {
        var finite = values.Where(IsFinite).Select(x => x!.Value).ToArray();
        var distinct = finite.Distinct().Order().ToArray();
        int repeats = 0;
        for (int i = 1; i < values.Count; i++)
            if (IsFinite(values[i - 1]) && IsFinite(values[i]) &&
                values[i - 1]!.Value == values[i]!.Value) repeats++;
        var steps = new List<double>();
        for (int i = 1; i < distinct.Length; i++)
        {
            var step = distinct[i] - distinct[i - 1];
            if (step > 0 && double.IsFinite(step)) steps.Add(step);
        }
        return new(name, Distribution(finite), distinct.Length, repeats,
            steps.Count == 0 ? null : steps.Min());
    }

    static CharacterizationDistribution Distribution(IReadOnlyList<double> values)
    {
        if (values.Count == 0) return new(0, null, null, null, null, null, null);
        var mean = values.Average();
        var variance = values.Sum(x => (x - mean) * (x - mean)) / values.Count;
        return new(values.Count, mean, variance, Math.Sqrt(variance),
            Percentile(values, .05), Percentile(values, .5), Percentile(values, .95));
    }

    static IReadOnlyList<CharacterizationCurrentBand> CurrentBands(
        IReadOnlyList<RecordedTelemetryRow> rows, IReadOnlyList<double?> residuals)
    {
        return rows.Select((row, index) => (row, index))
            .Where(x => IsFinite(x.row.ConnectorCurrentAmps))
            .GroupBy(x => Math.Floor(x.row.ConnectorCurrentAmps!.Value / 10) * 10)
            .OrderBy(x => x.Key)
            .Select(group =>
            {
                var voltages = group.Select(x => x.row.VoltageVolts).Where(IsFinite)
                    .Select(x => x!.Value).ToArray();
                var bandResiduals = group.Select(x => residuals[x.index]).Where(IsFinite)
                    .Select(x => x!.Value).ToArray();
                return new CharacterizationCurrentBand(group.Key, group.Key + 10, group.Count(),
                    Distribution(voltages).Variance, Distribution(bandResiduals).Variance);
            }).ToArray();
    }

    static CharacterizationCrossRailTiming CrossRailTiming(IReadOnlyList<RecordedTelemetryRow> rows)
    {
        int paired = rows.Count(x => IsFinite(x.VoltageVolts) && IsFinite(x.PcieVoltageVolts));
        int either = rows.Count(x => IsFinite(x.VoltageVolts) || IsFinite(x.PcieVoltageVolts));
        return new(paired == 0 ? "NO_PAIRED_RAILS" : "ROW_ALIGNED_SOURCE_TIMING_UNVERIFIED",
            paired, either - paired,
            "Only host-row alignment is available; independent native update timestamps were not captured.");
    }

    static CharacterizationDrift Drift(IReadOnlyList<RecordedTelemetryRow> rows)
    {
        const double windowSeconds = 3600;
        if (rows.Count == 0) return new("NO_DATA", windowSeconds, null, null, null);
        var start = rows[0].TimestampUtc;
        var end = rows[^1].TimestampUtc;
        if ((end - start).TotalSeconds < windowSeconds * 2)
            return new("INSUFFICIENT_DURATION", windowSeconds, null, null, null);
        var first = rows.Where(x => x.TimestampUtc <= start.AddSeconds(windowSeconds))
            .Select(x => x.VoltageVolts).Where(IsFinite).Select(x => x!.Value).ToArray();
        var last = rows.Where(x => x.TimestampUtc >= end.AddSeconds(-windowSeconds))
            .Select(x => x.VoltageVolts).Where(IsFinite).Select(x => x!.Value).ToArray();
        if (first.Length == 0 || last.Length == 0)
            return new("INSUFFICIENT_COVERAGE", windowSeconds, null, null, null);
        var firstMean = first.Average(); var lastMean = last.Average();
        return new("DESCRIPTIVE_ONLY", windowSeconds, firstMean, lastMean, lastMean - firstMean);
    }

    static CharacterizationPollingEvidence Polling(IReadOnlyList<RecordedTelemetryRow> rows)
    {
        var behavior = Behavior("input_voltage_v", rows.Select(x => x.VoltageVolts).ToArray());
        var latencies = rows.Select(x => x.PollLatencySeconds).Where(IsFinite)
            .Select(x => x!.Value).ToArray();
        var reportedRepeats = rows.Select(x => x.ConsecutiveIdenticalObservations)
            .Where(x => x.HasValue).Select(x => x!.Value).ToArray();
        int unhealthy = rows.Count(x => !string.IsNullOrWhiteSpace(x.AcquisitionHealth) &&
            !x.AcquisitionHealth!.Equals("HEALTHY", StringComparison.OrdinalIgnoreCase));
        return new(behavior.UniqueValueCount, behavior.RepeatedUpdateCount,
            Distribution(latencies), reportedRepeats.Length == 0 ? null : reportedRepeats.Max(), unhealthy,
            latencies.Length > 0 ? "RECORDED_HOST_POLL_LATENCY" : "NOT_CAPTURED",
            "NOT_CAPTURED", "NOT_CAPTURED",
            rows.Any(x => !string.IsNullOrWhiteSpace(x.AcquisitionHealth))
                ? "RECORDED_ACQUISITION_STATUS_ONLY" : "NOT_CAPTURED");
    }

    static CharacterizationDetectorEvaluation EvaluateDetectors(
        IReadOnlyList<RecordedTelemetryRow> rows, IReadOnlyList<double?> residuals)
    {
        var detector = ResidualDetectorFactory.CreateComposite();
        int transitions = 0; bool alerted = false;
        for (int i = 0; i < rows.Count; i++)
        {
            if (!IsFinite(residuals[i])) continue;
            var result = detector.Update(ResidualObservation.Available(rows[i].TimestampUtc,
                residuals[i]!.Value));
            bool next = result.IsAlert;
            if (next && !alerted) transitions++;
            alerted = next;
        }
        double observedSeconds = 0;
        for (int i = 1; i < rows.Count; i++)
        {
            var seconds = (rows[i].TimestampUtc - rows[i - 1].TimestampUtc).TotalSeconds;
            if (seconds > 0 && seconds <= 5) observedSeconds += seconds;
        }
        double? hours = observedSeconds > 0 ? observedSeconds / 3600 : null;
        double? rate = hours is double duration && duration > 0 ? transitions / duration : null;
        var options = new ResidualDetectorOptions();
        return new(residuals.Any(IsFinite) ? "DESCRIPTIVE_RECORDED_RESIDUAL" : "NO_RESIDUAL_DATA",
            hours, transitions, rate, options.FastDroopThresholdVolts,
            options.EwmaDroopThresholdVolts, false,
            new[] { 10, 20, 50, 100, 200 }.Select(Probe).ToArray());
    }

    static CharacterizationThresholdProbe Probe(int millivolts)
    {
        var fast = new FastResidualDetector(); var ewma = new EwmaResidualDetector();
        var epoch = DateTimeOffset.UnixEpoch; var injectedAt = epoch.AddSeconds(12);
        DateTimeOffset? fastAt = null, ewmaAt = null;
        for (int i = 0; i < 42; i++)
        {
            var timestamp = epoch.AddSeconds(i);
            var residual = i < 12 ? 0 : -millivolts / 1000.0;
            var observation = ResidualObservation.Available(timestamp, residual);
            var fastResult = fast.Update(observation); var ewmaResult = ewma.Update(observation);
            if (fastResult.IsAlert && fastAt is null) fastAt = timestamp;
            if (ewmaResult.IsAlert && ewmaAt is null) ewmaAt = timestamp;
        }
        return new(millivolts, fastAt is not null,
            fastAt is DateTimeOffset f ? (f - injectedAt).TotalSeconds : null,
            ewmaAt is not null,
            ewmaAt is DateTimeOffset e ? (e - injectedAt).TotalSeconds : null);
    }

    static int OutOfOrderCount(IReadOnlyList<RecordedTelemetryRow> rows)
    {
        int count = 0;
        for (int i = 1; i < rows.Count; i++)
            if (rows[i].TimestampUtc < rows[i - 1].TimestampUtc) count++;
        return count;
    }

    static double[] PositiveIntervals(IReadOnlyList<DateTimeOffset> timestamps)
    {
        var values = new List<double>(Math.Max(0, timestamps.Count - 1));
        for (int i = 1; i < timestamps.Count; i++)
        {
            var seconds = (timestamps[i] - timestamps[i - 1]).TotalSeconds;
            if (double.IsFinite(seconds) && seconds > 0) values.Add(seconds);
        }
        return values.ToArray();
    }

    static double? Mean(IReadOnlyList<double> values) =>
        values.Count == 0 ? null : values.Average();

    static double? Median(IReadOnlyList<double> values) => Percentile(values, .5);

    static double? Percentile(IReadOnlyList<double> values, double quantile)
    {
        if (values.Count == 0) return null;
        var sorted = values.Order().ToArray();
        var index = (sorted.Length - 1) * quantile;
        var lower = (int)Math.Floor(index); var upper = (int)Math.Ceiling(index);
        return lower == upper ? sorted[lower] : sorted[lower] + (sorted[upper] - sorted[lower]) * (index - lower);
    }

    static bool IsFiniteTimestamp(RecordedTelemetryRow row) =>
        row.TimestampUtc != default && row.TimestampUtc.Offset >= TimeSpan.FromHours(-14) &&
        row.TimestampUtc.Offset <= TimeSpan.FromHours(14);

    static bool IsFinite(double? value) => value is double number && double.IsFinite(number);

    sealed record SensorValues(string Name, IReadOnlyList<double?> Values);

    static IReadOnlyList<SensorValues> BuildSensorValues(IReadOnlyList<RecordedTelemetryRow> rows) =>
    [
        new("load_w", rows.Select(x => x.LoadWatts).ToArray()),
        new("input_voltage_v", rows.Select(x => x.VoltageVolts).ToArray()),
        new("temperature_c", rows.Select(x => x.TemperatureC).ToArray()),
        new("board_power_w", rows.Select(x => x.BoardPowerWatts).ToArray()),
        new("connector_power_w", rows.Select(x => x.ConnectorPowerWatts).ToArray()),
        new("connector_current_a", rows.Select(x => x.ConnectorCurrentAmps).ToArray()),
        new("pcie_voltage_v", rows.Select(x => x.PcieVoltageVolts).ToArray()),
        new("pcie_current_a", rows.Select(x => x.PcieCurrentAmps).ToArray()),
        new("pcie_power_w", rows.Select(x => x.PciePowerWatts).ToArray()),
        new("utilization_pct", rows.Select(x => x.UtilizationPercent).ToArray()),
        new("power_limit_w", rows.Select(x => x.PowerLimitWatts).ToArray()),
    ];

    sealed record ParsedFile(
        IReadOnlyList<RecordedTelemetryRow> Rows,
        int RowsRead,
        int InvalidRows,
        bool SyntheticMarker);

    static ParsedFile ParseFile(string path)
    {
        var extension = Path.GetExtension(path);
        if (extension.Equals(".csv", StringComparison.OrdinalIgnoreCase)) return ParseCsvFile(path);
        return ParseJsonFile(path);
    }

    static ParsedFile ParseCsvFile(string path)
    {
        var records = CsvRecords(File.ReadAllText(path)).ToArray();
        if (records.Length == 0) return new([], 0, 0, SyntheticPath(path));
        string[] headers;
        try { headers = Csv.Parse(records[0].TrimStart('\uFEFF')); }
        catch (FormatException) { return new([], 0, Math.Max(0, records.Length - 1), SyntheticPath(path)); }
        var rows = new List<RecordedTelemetryRow>(); int invalid = 0; bool synthetic = SyntheticPath(path);
        for (int i = 1; i < records.Length; i++)
        {
            try
            {
                var cells = Csv.Parse(records[i]);
                if (cells.Length < headers.Length || !TryCsvRow(headers, cells, Path.GetFileName(path), out var row)) { invalid++; continue; }
                rows.Add(row); synthetic |= row.IsSynthetic;
            }
            catch (FormatException) { invalid++; }
        }
        return new(rows, records.Length - 1, invalid, synthetic);
    }

    static IEnumerable<string> CsvRecords(string text)
    {
        var record = new StringBuilder(); bool quoted = false;
        for (int i = 0; i < text.Length; i++)
        {
            var character = text[i]; record.Append(character);
            if (character == '"')
            {
                if (quoted && i + 1 < text.Length && text[i + 1] == '"') { record.Append(text[++i]); continue; }
                quoted = !quoted;
            }
            if (character == '\n' && !quoted)
            {
                yield return record.ToString().TrimEnd('\r', '\n');
                record.Clear();
            }
        }
        if (record.Length > 0) yield return record.ToString().TrimEnd('\r', '\n');
    }

    static bool TryCsvRow(string[] headers, string[] cells, string source, out RecordedTelemetryRow row)
    {
        string Get(params string[] names)
        {
            foreach (var name in names)
            {
                var index = Array.FindIndex(headers, x => Normalize(x) == Normalize(name));
                if (index >= 0 && index < cells.Length) return cells[index].Trim();
            }
            return "";
        }
        if (!TryTimestamp(Get("timestamp_utc", "timestamp", "time_utc", "time"), out var timestamp))
        { row = default!; return false; }
        var analysis = Number(Get("analysis_power_w", "load_w", "power_w"));
        var connectorPower = Number(Get("connector_power_w"));
        var boardPower = Number(Get("board_power_w"));
        double? load = null; string? loadSource = null;
        if (analysis is double analysisValue) { load = analysisValue; loadSource = "analysis_power_w"; }
        else if (connectorPower is double connectorValue) { load = connectorValue; loadSource = "connector_power_w"; }
        else if (boardPower is double boardValue) { load = boardValue; loadSource = "board_power_w"; }
        var voltage = Number(Get("input_voltage_v", "connector_voltage_v", "voltage_v"));
        var temperature = Number(Get("gpu_temp_c", "temperature_c", "gpu_temperature_c"));
        var extras = ParseExtras(Get("extra_voltages_json"));
        var sourceValue = string.Join(" ", Get("gpu_uuid"), Get("voltage_source"), Get("analysis_power_source"), Get("detail"));
        row = new(timestamp, load, voltage, temperature, loadSource,
            IsSyntheticText(sourceValue), source, boardPower, connectorPower,
            Number(Get("connector_current_a")) ?? Extra(extras, "12vhpwr_a", "connector_current_a"),
            Number(Get("pcie_voltage_v")) ?? Extra(extras, "pcie_12v_v", "pcie_voltage_v"),
            Number(Get("pcie_current_a")) ?? Extra(extras, "pcie_12v_a", "pcie_current_a"),
            Number(Get("pcie_power_w")) ?? Extra(extras, "pcie_12v_w", "pcie_power_w"),
            Number(Get("utilization_pct", "gpu_utilization_pct")),
            Number(Get("power_limit_w")),
            Number(Get("poll_latency_seconds")),
            Integer(Get("consecutive_identical_observations")),
            Get("acquisition_health"));
        return true;
    }

    static ParsedFile ParseJsonFile(string path)
    {
        var rows = new List<RecordedTelemetryRow>(); int read = 0; int invalid = 0;
        bool synthetic = SyntheticPath(path);
        var text = File.ReadAllText(path);
        if (Path.GetExtension(path).Equals(".jsonl", StringComparison.OrdinalIgnoreCase) ||
            Path.GetExtension(path).Equals(".ndjson", StringComparison.OrdinalIgnoreCase))
        {
            foreach (var line in text.Split('\n'))
            {
                var candidate = line.Trim(); if (candidate.Length == 0) continue;
                try
                {
                    using var doc = JsonDocument.Parse(candidate);
                    WalkJson(doc.RootElement, rows, ref read, ref invalid, ref synthetic, SyntheticPath(path));
                }
                catch (JsonException) { invalid++; }
            }
        }
        else
        {
            try
            {
                using var doc = JsonDocument.Parse(text);
                WalkJson(doc.RootElement, rows, ref read, ref invalid, ref synthetic, SyntheticPath(path));
            }
            catch (JsonException) { invalid++; }
        }
        return new(rows, read, invalid, synthetic);
    }

    static void WalkJson(JsonElement element, List<RecordedTelemetryRow> rows,
        ref int read, ref int invalid, ref bool synthetic, bool inheritedSynthetic)
    {
        if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var child in element.EnumerateArray()) WalkJson(child, rows, ref read, ref invalid, ref synthetic, inheritedSynthetic);
            return;
        }
        if (element.ValueKind != JsonValueKind.Object) return;
        var marked = inheritedSynthetic || IsSyntheticJson(element);
        synthetic |= marked;
        bool nestedRows = false;
        foreach (var property in element.EnumerateObject())
            if (property.Value.ValueKind == JsonValueKind.Array &&
                (property.Name.Equals("rows", StringComparison.OrdinalIgnoreCase) ||
                 property.Name.Equals("samples", StringComparison.OrdinalIgnoreCase) ||
                 property.Name.Equals("telemetry", StringComparison.OrdinalIgnoreCase)))
            {
                nestedRows = true;
                WalkJson(property.Value, rows, ref read, ref invalid, ref synthetic, marked);
            }
        if (nestedRows) return;
        read++;
        if (!TryJsonRow(element, marked, out var row)) { invalid++; return; }
        rows.Add(row); synthetic |= row.IsSynthetic;
    }

    static bool TryJsonRow(JsonElement root, bool inheritedSynthetic, out RecordedTelemetryRow row)
    {
        if (!TryTimestamp(Text(root, "timestamp_utc", "timestamp", "time_utc", "time").FirstOrDefault() ?? "", out var timestamp))
        { row = default!; return false; }
        var gpu = Object(root, "gpu"); var voltageObject = Object(root, "voltage");
        var analysis = Number(root, "analysis_power_w", "load_w", "power_w") ?? Number(gpu, "power_w", "Power");
        var connectorPower = Number(root, "connector_power_w");
        var boardPower = Number(root, "board_power_w") ?? Number(gpu, "power_w", "Power");
        double? load = null; string? source = null;
        if (analysis is double analysisValue) { load = analysisValue; source = "analysis_power_w"; }
        else if (connectorPower is double connectorValue) { load = connectorValue; source = "connector_power_w"; }
        else if (boardPower is double boardValue) { load = boardValue; source = "board_power_w"; }
        var voltage = Number(root, "input_voltage_v", "connector_voltage_v", "voltage_v") ?? Number(voltageObject, "Volts", "voltage_v");
        var temperature = Number(root, "gpu_temp_c", "temperature_c", "gpu_temperature_c") ?? Number(gpu, "temperature_c", "Temperature");
        var extras = Object(root, "extra_voltages");
        var sourceText = string.Join(" ", Text(root, "gpu_uuid", "voltage_source", "analysis_power_source", "source", "detail"));
        row = new(timestamp, load, voltage, temperature, source, inheritedSynthetic || IsSyntheticText(sourceText),
            Text(root, "source", "voltage_source").FirstOrDefault(), boardPower, connectorPower,
            Number(root, "connector_current_a") ?? Number(extras, "12vhpwr_a", "connector_current_a"),
            Number(root, "pcie_voltage_v") ?? Number(extras, "pcie_12v_v", "pcie_voltage_v"),
            Number(root, "pcie_current_a") ?? Number(extras, "pcie_12v_a", "pcie_current_a"),
            Number(root, "pcie_power_w") ?? Number(extras, "pcie_12v_w", "pcie_power_w"),
            Number(root, "utilization_pct") ?? Number(gpu, "utilization_pct", "Utilization"),
            Number(root, "power_limit_w") ?? Number(gpu, "power_limit_w", "Limit"),
            Number(root, "poll_latency_seconds", "latency_seconds"),
            Integer(root, "consecutive_identical_observations"),
            Text(root, "acquisition_health").FirstOrDefault());
        return true;
    }

    static Dictionary<string, double> ParseExtras(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return new(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var doc = JsonDocument.Parse(text);
            return doc.RootElement.ValueKind == JsonValueKind.Object
                ? doc.RootElement.EnumerateObject().Where(x => Number(x.Value) is double)
                    .ToDictionary(x => Normalize(x.Name), x => Number(x.Value)!.Value, StringComparer.OrdinalIgnoreCase)
                : new(StringComparer.OrdinalIgnoreCase);
        }
        catch (JsonException) { return new(StringComparer.OrdinalIgnoreCase); }
    }

    static double? Extra(IReadOnlyDictionary<string, double> values, params string[] names) =>
        names.Select(Normalize).FirstOrDefault(values.ContainsKey) is string name && values.TryGetValue(name, out var value) ? value : null;

    static JsonElement Object(JsonElement root, string name) =>
        root.ValueKind == JsonValueKind.Object && TryProperty(root, name, out var value) && value.ValueKind == JsonValueKind.Object
            ? value : default;

    static string[] Text(JsonElement root, params string[] names) =>
        names.Select(name => TextOne(root, name)).Where(x => x.Length > 0).ToArray();

    static string TextOne(JsonElement root, string name) =>
        root.ValueKind == JsonValueKind.Object && TryProperty(root, name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? "" : "";

    static double? Number(JsonElement root, params string[] names) =>
        names.Select(name => root.ValueKind == JsonValueKind.Object && TryProperty(root, name, out var value) ? Number(value) : null)
            .FirstOrDefault(x => x.HasValue);

    static double? Number(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number) && double.IsFinite(number)) return number;
        if (value.ValueKind == JsonValueKind.String &&
            double.TryParse(value.GetString(), NumberStyles.Float, Invariant, out number) && double.IsFinite(number)) return number;
        return null;
    }

    static double? Number(string value) =>
        double.TryParse(value, NumberStyles.Float, Invariant, out var number) && double.IsFinite(number) ? number : null;

    static int? Integer(string value) =>
        int.TryParse(value, NumberStyles.Integer, Invariant, out var number) ? number : null;

    static int? Integer(JsonElement root, params string[] names)
    {
        var number = Number(root, names);
        return number is double value && value >= int.MinValue && value <= int.MaxValue &&
            Math.Truncate(value) == value ? (int)value : null;
    }

    static bool TryTimestamp(string value, out DateTimeOffset timestamp) =>
        DateTimeOffset.TryParse(value, Invariant, DateTimeStyles.RoundtripKind, out timestamp) &&
        timestamp != default && timestamp.Offset >= TimeSpan.FromHours(-14) && timestamp.Offset <= TimeSpan.FromHours(14);

    static string Normalize(string value) =>
        new(value.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    static bool TryProperty(JsonElement root, string name, out JsonElement value)
    {
        if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty(name, out value)) return true;
        if (root.ValueKind == JsonValueKind.Object)
            foreach (var property in root.EnumerateObject())
                if (property.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                { value = property.Value; return true; }
        value = default;
        return false;
    }

    static bool SyntheticPath(string path) => IsSyntheticText(path);

    static bool IsSyntheticText(IEnumerable<string> values) => values.Any(IsSyntheticText);

    static bool IsSyntheticText(string value) =>
        value.Contains("synthetic", StringComparison.OrdinalIgnoreCase) ||
        value.Contains("fixture", StringComparison.OrdinalIgnoreCase) ||
        value.Contains("demo", StringComparison.OrdinalIgnoreCase);

    static bool IsSyntheticJson(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object) return false;
        if (TryProperty(root, "synthetic", out var value) && value.ValueKind == JsonValueKind.True) return true;
        return IsSyntheticText(Text(root, "evidence", "source", "voltage_source", "detail", "gpu_uuid"));
    }
}

/// <summary>Short alias for callers that prefer a verb-style class name.</summary>
public static class RecordedDataCharacterizer
{
    public static RecordedDataCharacterizationReport Analyze(IEnumerable<RecordedTelemetryRow> rows,
        RecordedDataCharacterizationOptions? options = null, CharacterizationEvidence? evidence = null) =>
        RecordedDataCharacterization.Analyze(rows, options, evidence);

    public static RecordedDataCharacterizationReport AnalyzeFile(string path,
        RecordedDataCharacterizationOptions? options = null, CharacterizationEvidence? evidence = null) =>
        RecordedDataCharacterization.AnalyzeFile(path, options, evidence);

    public static RecordedDataCharacterizationReport AnalyzeFiles(IEnumerable<string> paths,
        RecordedDataCharacterizationOptions? options = null, CharacterizationEvidence? evidence = null) =>
        RecordedDataCharacterization.AnalyzeFiles(paths, options, evidence);
}
