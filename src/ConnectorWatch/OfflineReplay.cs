using System.Collections.ObjectModel;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ConnectorWatch;

/// <summary>
/// The expected state attached to one offline replay observation.  The
/// expectation is an annotation supplied by the fixture author; it is never
/// inferred from the detector result.  Keeping the two independent makes the
/// resulting confusion counts useful when a threshold or detector changes.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ReplayExpectedState
{
    Normal,
    Alert,
    Unavailable,
}

/// <summary>One deterministic observation in an offline replay log.</summary>
public sealed record ReplaySample(
    DateTimeOffset TimestampUtc,
    double? LoadWatts,
    double? VoltageVolts,
    ReplayExpectedState ExpectedState,
    bool IsFresh = true,
    bool RestartBefore = false,
    string? Annotation = null,
    bool AcceptReferenceBefore = false)
{
    public bool HasFiniteVoltage => VoltageVolts is double value && double.IsFinite(value);

    public bool HasFiniteLoad => LoadWatts is null ||
        (LoadWatts is double value && double.IsFinite(value));

    internal void Validate(int index, DateTimeOffset? previous)
    {
        var timestamp = TimestampUtc.ToUniversalTime();
        if (previous.HasValue && timestamp < previous.Value)
            throw new FormatException($"Replay sample {index} moves backwards in time.");
        if (timestamp == default)
            throw new FormatException($"Replay sample {index} has no timestamp.");
        if (LoadWatts is double load && !double.IsFinite(load))
            throw new FormatException($"Replay sample {index} has a non-finite load.");
        if (VoltageVolts is double voltage && !double.IsFinite(voltage))
            throw new FormatException($"Replay sample {index} has a non-finite voltage.");
        if (IsFresh && VoltageVolts is null)
            throw new FormatException($"Replay sample {index} marks a missing voltage fresh.");
    }
}

/// <summary>
/// A named, deterministic sequence of samples.  Timestamps are part of the
/// fixture so the replay exercises elapsed-time confirmation and gap rules
/// without depending on the host clock.
/// </summary>
public sealed record ReplayScenario(
    string Name,
    string Description,
    IReadOnlyList<ReplaySample> Samples)
{
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Name))
            throw new ArgumentException("Replay scenario needs a name.", nameof(Name));
        if (Samples is null || Samples.Count == 0)
            throw new ArgumentException("Replay scenario needs at least one sample.", nameof(Samples));

        DateTimeOffset? previous = null;
        for (int i = 0; i < Samples.Count; i++)
        {
            Samples[i].Validate(i, previous);
            previous = Samples[i].TimestampUtc.ToUniversalTime();
        }
    }

    /// <summary>Serializes the scenario as a stable JSON object.</summary>
    public string ToJson(bool indented = true) =>
        JsonSerializer.Serialize(this, OfflineReplayJson.Options(indented));

    /// <summary>
    /// Serializes samples as newline-delimited JSON.  The first line is a
    /// metadata record and every subsequent line is a sample.  This format is
    /// convenient for checked-in fixtures and permits a producer to append a
    /// sample without rewriting a large object.
    /// </summary>
    public string ToNdjson()
    {
        Validate();
        var builder = new StringBuilder();
        builder.Append(JsonSerializer.Serialize(
            new ReplayLogHeader(Name, Description), OfflineReplayJson.Options(false)));
        builder.Append('\n');
        foreach (var sample in Samples)
        {
            builder.Append(JsonSerializer.Serialize(sample, OfflineReplayJson.Options(false)));
            builder.Append('\n');
        }
        return builder.ToString();
    }

    public static ReplayScenario FromJson(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) throw new FormatException("Replay JSON is empty.");
        var scenario = JsonSerializer.Deserialize<ReplayScenario>(json, OfflineReplayJson.Options(false))
            ?? throw new FormatException("Replay JSON did not contain a scenario.");
        scenario.Validate();
        return scenario;
    }

    public static ReplayScenario FromNdjson(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) throw new FormatException("Replay NDJSON is empty.");
        ReplayLogHeader? header = null;
        var samples = new List<ReplaySample>();
        using var reader = new StringReader(text);
        string? line;
        int lineNumber = 0;
        while ((line = reader.ReadLine()) is not null)
        {
            lineNumber++;
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                if (header is null)
                {
                    header = JsonSerializer.Deserialize<ReplayLogHeader>(line,
                        OfflineReplayJson.Options(false));
                    if (header is null || string.IsNullOrWhiteSpace(header.Name))
                        throw new FormatException("NDJSON header is invalid.");
                }
                else
                {
                    var sample = JsonSerializer.Deserialize<ReplaySample>(line,
                        OfflineReplayJson.Options(false))
                        ?? throw new FormatException("NDJSON sample is null.");
                    samples.Add(sample);
                }
            }
            catch (JsonException ex)
            {
                throw new FormatException($"Replay NDJSON line {lineNumber} is invalid.", ex);
            }
        }
        if (header is null) throw new FormatException("Replay NDJSON has no header.");
        var scenario = new ReplayScenario(header.Name, header.Description, samples);
        scenario.Validate();
        return scenario;
    }

    public static ReplayScenario ReadJson(string path) =>
        FromJson(File.ReadAllText(path, Encoding.UTF8));

    public static ReplayScenario ReadNdjson(string path) =>
        FromNdjson(File.ReadAllText(path, Encoding.UTF8));

    public void WriteJson(string path, bool indented = true) =>
        File.WriteAllText(path, ToJson(indented), new UTF8Encoding(false));

    public void WriteNdjson(string path) =>
        File.WriteAllText(path, ToNdjson(), new UTF8Encoding(false));
}

sealed record ReplayLogHeader(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("description")] string Description);

/// <summary>JSON options shared by replay inputs and metric reports.</summary>
public static class OfflineReplayJson
{
    public static JsonSerializerOptions Options(bool indented = true) => new()
    {
        WriteIndented = indented,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        NumberHandling = JsonNumberHandling.Strict,
        Converters = { new JsonStringEnumConverter() },
    };
}

/// <summary>One detector row produced by a replay.</summary>
public sealed record ReplayObservation(
    int Sequence,
    DateTimeOffset TimestampUtc,
    ReplayExpectedState ExpectedState,
    string Status,
    int? Bin,
    double? Reference,
    double? Median,
    double? P05,
    double? Drop,
    bool IsAlert,
    bool IsUnavailable,
    bool RestartBefore,
    string? Annotation,
    bool AcceptReferenceBefore);

/// <summary>
/// Machine-readable replay metrics.  Alert and availability confusion counts
/// are intentionally separate: an unavailable source is not an alert and an
/// alert is not evidence that a missing source was handled correctly.
/// </summary>
public sealed record ReplayMetrics(
    string Scenario,
    int TotalSamples,
    int FreshSamples,
    int StaleOrMissingSamples,
    int RestartCount,
    int ExpectedAlertSamples,
    int ActualAlertSamples,
    int TruePositiveSamples,
    int FalsePositiveSamples,
    int FalseNegativeSamples,
    int ExpectedUnavailableSamples,
    int ActualUnavailableSamples,
    int AvailabilityTruePositiveSamples,
    int AvailabilityFalsePositiveSamples,
    int AvailabilityFalseNegativeSamples,
    int ExpectedAlertSegments,
    int DetectedAlertSegments,
    int MissedAlertSegments,
    double? FirstDetectionLatencySeconds,
    double? RecoveryLatencySeconds,
    DateTimeOffset? FirstDetectionUtc,
    IReadOnlyDictionary<string, int> StatusCounts,
    IReadOnlyDictionary<string, int> ExpectedStateCounts)
{
    [JsonPropertyName("alert_precision")] public double? AlertPrecision =>
        ActualAlertSamples == 0 ? null : TruePositiveSamples / (double)ActualAlertSamples;

    [JsonPropertyName("alert_recall")] public double? AlertRecall =>
        ExpectedAlertSamples == 0 ? null : TruePositiveSamples / (double)ExpectedAlertSamples;

    [JsonPropertyName("availability_precision")] public double? AvailabilityPrecision =>
        ActualUnavailableSamples == 0 ? null :
            AvailabilityTruePositiveSamples / (double)ActualUnavailableSamples;

    [JsonPropertyName("availability_recall")] public double? AvailabilityRecall =>
        ExpectedUnavailableSamples == 0 ? null :
            AvailabilityTruePositiveSamples / (double)ExpectedUnavailableSamples;

    public string ToJson(bool indented = true) =>
        JsonSerializer.Serialize(this, OfflineReplayJson.Options(indented));
}

/// <summary>All data returned by one deterministic replay.</summary>
public sealed record ReplayRun(
    ReplayScenario Scenario,
    ReplayMetrics Metrics,
    IReadOnlyList<ReplayObservation> Observations)
{
    public string ToJson(bool indented = true) =>
        JsonSerializer.Serialize(this, OfflineReplayJson.Options(indented));

    public string MetricsJson(bool indented = true) => Metrics.ToJson(indented);
}

/// <summary>A named detector configuration used by threshold comparisons.</summary>
public sealed record ReplayThresholdProfile(
    string Name,
    double ShiftVolts = RailDetectionThresholdDefaults.ShiftVolts,
    double SuddenDroopVolts = RailDetectionThresholdDefaults.SuddenDroopVolts,
    int SustainSamples = 3,
    int StableSamples = 3,
    int BaselineSamples = 20,
    int WindowSamples = 5,
    int WindowMaxAgeSeconds = 1800,
    double? GrossUnderVoltageV = null,
    double? GrossOverVoltageV = null,
    double CoarseConfirmationSeconds = 0,
    int CoarseConfirmationSamples = 1)
{
    public Config ToConfig()
    {
        var config = OfflineReplayFixtures.CreateConfig();
        config.ShiftVolts = ShiftVolts;
        config.SuddenDroopVolts = SuddenDroopVolts;
        config.SustainSamples = SustainSamples;
        config.StableSamples = StableSamples;
        config.BaselineSamples = BaselineSamples;
        config.WindowSamples = WindowSamples;
        config.WindowMaxAgeSeconds = WindowMaxAgeSeconds;
        config.GrossUnderVoltageV = GrossUnderVoltageV;
        config.GrossOverVoltageV = GrossOverVoltageV;
        config.CoarseConfirmationSeconds = CoarseConfirmationSeconds;
        config.CoarseConfirmationSamples = CoarseConfirmationSamples;
        config.Validate();
        return config;
    }
}

public sealed record ReplayComparison(
    ReplayThresholdProfile Profile,
    ReplayMetrics Metrics);

public sealed record ReplayComparisonReport(
    string Scenario,
    IReadOnlyList<ReplayComparison> Comparisons)
{
    public string ToJson(bool indented = true) =>
        JsonSerializer.Serialize(this, OfflineReplayJson.Options(indented));
};

/// <summary>Runs a scenario through the production <see cref="Analysis"/>.
/// No native libraries, GPU, wall clock, or daemon storage are touched.</summary>
public static class OfflineReplayRunner
{
    static readonly IReadOnlySet<string> AlertStatuses = new HashSet<string>(
        StringComparer.Ordinal)
    {
        "SUDDEN_DROOP",
        "BASELINE_SHIFT",
        "GROSS_UNDERVOLTAGE",
        "GROSS_OVERVOLTAGE",
        "RAPID_VOLTAGE_RISE",
    };

    static readonly IReadOnlySet<string> UnavailableStatuses = new HashSet<string>(
        StringComparer.Ordinal)
    {
        "VOLTAGE_UNAVAILABLE",
        "ANALYSIS_LOAD_UNAVAILABLE",
    };

    public static ReplayRun Run(ReplayScenario scenario, Config? config = null,
        bool preserveReferencesAcrossRestart = true)
    {
        scenario.Validate();
        var replayConfig = CopyConfig(config ?? OfflineReplayFixtures.CreateConfig());
        replayConfig.Validate();

        Analysis analysis = new(replayConfig);
        DateTimeOffset? previous = null;
        double elapsedClock = 0;
        int restarts = 0;
        var identity = Program.BuildReferenceIdentity(replayConfig, "none",
            "offline replay", AnalysisLoadSource.CONNECTOR_POWER);
        var rows = new List<ReplayObservation>(scenario.Samples.Count);

        for (int index = 0; index < scenario.Samples.Count; index++)
        {
            var sample = scenario.Samples[index];
            if (sample.RestartBefore)
            {
                restarts++;
                Dictionary<int, Bin>? saved = preserveReferencesAcrossRestart
                    ? CloneBins(analysis.Bins)
                    : null;
                analysis = new Analysis(replayConfig, saved);
                analysis.Gap();
            }

            if (sample.AcceptReferenceBefore)
            {
                // This is an explicit operator action in the replay log, not
                // an implicit promotion when learning reaches its sample
                // count. Keep it visible in the input and use the same
                // lifecycle seam as the daemon.
                var candidate = analysis.BuildCandidate(identity,
                    sample.TimestampUtc.ToUniversalTime());
                if (candidate is null || !candidate.IsQualified)
                    throw new InvalidOperationException(
                        $"Replay sample {index} requests acceptance without a qualified candidate.");
                var accepted = AcceptedReferenceModel.Freeze(candidate,
                    sample.TimestampUtc.ToUniversalTime(), "offline-replay",
                    "explicit fixture acceptance");
                analysis.ApplyAccepted(accepted);
            }

            if (previous.HasValue)
                elapsedClock += Math.Max(0,
                    (sample.TimestampUtc.ToUniversalTime() - previous.Value).TotalSeconds);
            bool fresh = sample.IsFresh && sample.HasFiniteVoltage;
            double voltage = sample.VoltageVolts ?? double.NaN;
            var result = analysis.Add(sample.TimestampUtc.ToUniversalTime(), sample.LoadWatts,
                voltage, elapsedClock, fresh);
            bool isAlert = AlertStatuses.Contains(result.Status);
            bool isUnavailable = UnavailableStatuses.Contains(result.Status);
            rows.Add(new ReplayObservation(index, sample.TimestampUtc.ToUniversalTime(),
                sample.ExpectedState, result.Status, result.Bin, result.Reference,
                result.Median, result.P05, result.Drop, isAlert, isUnavailable,
                sample.RestartBefore, sample.Annotation, sample.AcceptReferenceBefore));
            previous = sample.TimestampUtc.ToUniversalTime();
        }

        return new ReplayRun(scenario, BuildMetrics(scenario, rows, restarts), rows);
    }

    public static ReplayComparisonReport Compare(ReplayScenario scenario,
        IEnumerable<ReplayThresholdProfile> profiles)
    {
        if (profiles is null) throw new ArgumentNullException(nameof(profiles));
        var comparisons = profiles.Select(profile =>
            new ReplayComparison(profile, Run(scenario, profile.ToConfig()).Metrics)).ToArray();
        if (comparisons.Length == 0)
            throw new ArgumentException("At least one threshold profile is required.", nameof(profiles));
        return new ReplayComparisonReport(scenario.Name, comparisons);
    }

    static ReplayMetrics BuildMetrics(ReplayScenario scenario,
        IReadOnlyList<ReplayObservation> rows, int restartCount)
    {
        int expectedAlert = rows.Count(row => row.ExpectedState == ReplayExpectedState.Alert);
        int actualAlert = rows.Count(row => row.IsAlert);
        int truePositive = rows.Count(row => row.ExpectedState == ReplayExpectedState.Alert && row.IsAlert);
        int falsePositive = rows.Count(row => row.ExpectedState == ReplayExpectedState.Normal && row.IsAlert);
        int falseNegative = rows.Count(row => row.ExpectedState == ReplayExpectedState.Alert && !row.IsAlert);

        int expectedUnavailable = rows.Count(row => row.ExpectedState == ReplayExpectedState.Unavailable);
        int actualUnavailable = rows.Count(row => row.IsUnavailable);
        int availabilityTruePositive = rows.Count(row =>
            row.ExpectedState == ReplayExpectedState.Unavailable && row.IsUnavailable);
        int availabilityFalsePositive = rows.Count(row =>
            row.ExpectedState == ReplayExpectedState.Normal && row.IsUnavailable);
        int availabilityFalseNegative = rows.Count(row =>
            row.ExpectedState == ReplayExpectedState.Unavailable && !row.IsUnavailable);

        var expectedAlertSegments = Segments(rows, row => row.ExpectedState == ReplayExpectedState.Alert);
        var actualAlertSegments = Segments(rows, row => row.IsAlert);
        var detectedSegments = expectedAlertSegments.Count(segment =>
            segment.Any(index => rows[index].IsAlert));
        var firstExpected = expectedAlertSegments.FirstOrDefault();
        DateTimeOffset? firstDetectionUtc = null;
        double? firstLatency = null;
        double? recoveryLatency = null;
        if (firstExpected is not null)
        {
            int detectionIndex = firstExpected.FirstOrDefault(index => rows[index].IsAlert, -1);
            if (detectionIndex >= 0)
            {
                firstDetectionUtc = rows[detectionIndex].TimestampUtc;
                firstLatency = Math.Max(0, (rows[detectionIndex].TimestampUtc -
                    rows[firstExpected[0]].TimestampUtc).TotalSeconds);

                int expectedEnd = firstExpected[^1];
                int recoveryIndex = Enumerable.Range(expectedEnd + 1, rows.Count - expectedEnd - 1)
                    .FirstOrDefault(index => !rows[index].IsAlert && !rows[index].IsUnavailable, -1);
                if (recoveryIndex >= 0)
                    recoveryLatency = Math.Max(0, (rows[recoveryIndex].TimestampUtc -
                        rows[expectedEnd].TimestampUtc).TotalSeconds);
            }
        }

        var statuses = new SortedDictionary<string, int>(StringComparer.Ordinal);
        foreach (var row in rows)
            statuses[row.Status] = statuses.TryGetValue(row.Status, out int count) ? count + 1 : 1;
        var expectedStates = new SortedDictionary<string, int>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            var key = row.ExpectedState.ToString();
            expectedStates[key] = expectedStates.TryGetValue(key, out int count) ? count + 1 : 1;
        }

        return new ReplayMetrics(
            scenario.Name,
            rows.Count,
            rows.Count(index => scenario.Samples[index.Sequence].IsFresh &&
                scenario.Samples[index.Sequence].HasFiniteVoltage),
            rows.Count(index => !scenario.Samples[index.Sequence].IsFresh ||
                scenario.Samples[index.Sequence].VoltageVolts is null ||
                scenario.Samples[index.Sequence].LoadWatts is null),
            restartCount,
            expectedAlert,
            actualAlert,
            truePositive,
            falsePositive,
            falseNegative,
            expectedUnavailable,
            actualUnavailable,
            availabilityTruePositive,
            availabilityFalsePositive,
            availabilityFalseNegative,
            expectedAlertSegments.Count,
            detectedSegments,
            expectedAlertSegments.Count - detectedSegments,
            firstLatency,
            recoveryLatency,
            firstDetectionUtc,
            new ReadOnlyDictionary<string, int>(statuses),
            new ReadOnlyDictionary<string, int>(expectedStates));
    }

    static List<List<int>> Segments(IReadOnlyList<ReplayObservation> rows,
        Func<ReplayObservation, bool> predicate)
    {
        var segments = new List<List<int>>();
        List<int>? current = null;
        for (int index = 0; index < rows.Count; index++)
        {
            if (predicate(rows[index]))
            {
                current ??= [];
                current.Add(index);
            }
            else if (current is not null)
            {
                segments.Add(current);
                current = null;
            }
        }
        if (current is not null) segments.Add(current);
        return segments;
    }

    static Dictionary<int, Bin> CloneBins(Dictionary<int, Bin> source) =>
        source.ToDictionary(pair => pair.Key, pair => new Bin
        {
            Learning = [.. pair.Value.Learning],
            Reference = pair.Value.Reference,
            ReferenceP05 = pair.Value.ReferenceP05,
        });

    internal static Config CopyConfig(Config source) => new()
    {
        GpuUuid = source.GpuUuid,
        SampleSeconds = source.SampleSeconds,
        FlushSeconds = source.FlushSeconds,
        DataDirectory = source.DataDirectory,
        DesktopAlerts = source.DesktopAlerts,
        VoltageSource = source.VoltageSource,
        HwinfoCsv = source.HwinfoCsv,
        RailJson = source.RailJson,
        VoltageColumn = source.VoltageColumn,
        PowerColumn = source.PowerColumn,
        AnalysisLoadSource = source.AnalysisLoadSource,
        ExtraVoltageColumns = [.. source.ExtraVoltageColumns],
        DateColumn = source.DateColumn,
        TimeColumn = source.TimeColumn,
        TimestampFormat = source.TimestampFormat,
        Culture = source.Culture,
        Delimiter = source.Delimiter,
        MaxAgeSeconds = source.MaxAgeSeconds,
        BinWatts = source.BinWatts,
        MinAnalysisWatts = source.MinAnalysisWatts,
        StableSamples = source.StableSamples,
        BaselineSamples = source.BaselineSamples,
        WindowSamples = source.WindowSamples,
        WindowMaxAgeSeconds = source.WindowMaxAgeSeconds,
        ShiftVolts = source.ShiftVolts,
        SuddenDroopVolts = source.SuddenDroopVolts,
        GrossUnderVoltageV = source.GrossUnderVoltageV,
        GrossOverVoltageV = source.GrossOverVoltageV,
        CoarseConfirmationSeconds = source.CoarseConfirmationSeconds,
        CoarseConfirmationSamples = source.CoarseConfirmationSamples,
        LoadBoundaryHysteresisWatts = source.LoadBoundaryHysteresisWatts,
        SustainSamples = source.SustainSamples,
    };
}

/// <summary>
/// Deterministic scenarios used by the offline suite.  The fixtures are
/// generated from a fixed UTC epoch and contain no random noise, machine
/// identifiers, or wall-clock reads.
/// </summary>
public static class OfflineReplayFixtures
{
    public static readonly DateTimeOffset Epoch =
        new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    public static Config CreateConfig() => new()
    {
        GpuUuid = "GPU-00000000-0000-0000-0000-000000000001",
        VoltageSource = "none",
        SampleSeconds = 1,
        MaxAgeSeconds = 5,
        BinWatts = 25,
        MinAnalysisWatts = 100,
        StableSamples = 3,
        BaselineSamples = 20,
        WindowSamples = 5,
        WindowMaxAgeSeconds = 1800,
        ShiftVolts = .2,
        SuddenDroopVolts = .25,
        SustainSamples = 3,
        FlushSeconds = 1,
    };

    public static IReadOnlyList<ReplayScenario> All() =>
        [Healthy(), Noisy(), GradualSag(), AbruptDroop(), MissingAndStale(), Restart()];

    public static ReplayScenario Healthy() => Build("healthy",
        "Steady connector voltage at one qualified load band.",
        Tail(16, (_, _) => (12.0, true, false, false, ReplayExpectedState.Normal, "steady")));

    public static ReplayScenario Noisy() => Build("noisy",
        "Small deterministic noise remains below both comparison thresholds.",
        Tail(24, (index, _) => (index % 2 == 0 ? 11.98 : 12.02,
            true, false, false, ReplayExpectedState.Normal, "bounded-noise")));

    public static ReplayScenario GradualSag() => Build("gradual-sag",
        "A monotonic, sub-threshold-per-step sag eventually crosses the sustained comparison.",
        Tail(24, (index, _) =>
        {
            double voltage = 11.96 - Math.Min(index, 15) * .02;
            var expected = index >= 14 ? ReplayExpectedState.Alert : ReplayExpectedState.Normal;
            return (voltage, true, false, false, expected, "gradual-sag");
        }));

    public static ReplayScenario AbruptDroop() => Build("abrupt-droop",
        "A single fresh sample drops by more than the rapid discontinuity threshold.",
        Tail(12, (index, _) => (11.70, true, false, false, ReplayExpectedState.Alert,
            index == 0 ? "abrupt-droop" : "droop-held")));

    public static ReplayScenario MissingAndStale()
    {
        var scenario = Build("missing-stale",
            "Missing and stale voltage observations become unavailable and then recover after settling.",
            MissingStaleTail());
        // Keep one separate missing-load case in the same source-gap fixture.
        // A valid voltage with a missing selected load must not be confused
        // with a stale voltage sample.
        var samples = scenario.Samples.ToList();
        int missingLoadIndex = CreateConfig().StableSamples + CreateConfig().BaselineSamples + 4;
        var original = samples[missingLoadIndex];
        samples[missingLoadIndex] = original with
        {
            LoadWatts = null,
            VoltageVolts = 12.0,
            IsFresh = true,
            Annotation = "missing-load",
        };
        return scenario with { Samples = samples };
    }

    public static ReplayScenario Restart() => Build("restart",
        "A daemon restart preserves the learned reference but requires fresh load qualification.",
        RestartTail());

    static ReplayScenario Build(string name, string description,
        IEnumerable<(double? Voltage, bool IsFresh, bool RestartBefore,
            bool AcceptReferenceBefore, ReplayExpectedState Expected, string Note)> tail)
    {
        var samples = Baseline();
        int offset = samples.Count;
        foreach (var item in tail.Select((value, index) => (value, index)))
        {
            samples.Add(new ReplaySample(Epoch.AddSeconds(offset + item.index), 440,
                item.value.Voltage, item.value.Expected, item.value.IsFresh,
                item.value.RestartBefore, item.value.Note,
                item.value.AcceptReferenceBefore || item.index == 0));
        }
        return new ReplayScenario(name, description, samples);
    }

    static List<ReplaySample> Baseline()
    {
        var config = CreateConfig();
        var samples = new List<ReplaySample>(config.StableSamples + config.BaselineSamples);
        for (int index = 0; index < config.StableSamples + config.BaselineSamples; index++)
            samples.Add(new ReplaySample(Epoch.AddSeconds(index), 440, 12.0,
                ReplayExpectedState.Normal, Annotation: index < config.StableSamples
                    ? "qualification" : "baseline"));
        return samples;
    }

    static IEnumerable<(double? Voltage, bool IsFresh, bool RestartBefore,
        bool AcceptReferenceBefore, ReplayExpectedState Expected, string Note)> Tail(
        int count, Func<int, int, (double Voltage, bool IsFresh, bool RestartBefore,
            bool AcceptReferenceBefore, ReplayExpectedState Expected, string Note)> factory)
    {
        for (int index = 0; index < count; index++)
            yield return factory(index, count);
    }

    static IEnumerable<(double? Voltage, bool IsFresh, bool RestartBefore,
        bool AcceptReferenceBefore, ReplayExpectedState Expected, string Note)> MissingStaleTail()
    {
        for (int i = 0; i < 4; i++)
            yield return (null, false, false, false, ReplayExpectedState.Unavailable, "missing-voltage");
        for (int i = 0; i < 3; i++)
            yield return (12.0, false, false, false, ReplayExpectedState.Unavailable, "stale-voltage");
        for (int i = 0; i < 12; i++)
            yield return (12.0, true, false, false, ReplayExpectedState.Normal, "recovered");
    }

    static IEnumerable<(double? Voltage, bool IsFresh, bool RestartBefore,
        bool AcceptReferenceBefore, ReplayExpectedState Expected, string Note)> RestartTail()
    {
        for (int i = 0; i < 5; i++)
            yield return (12.0, true, false, false, ReplayExpectedState.Normal, "pre-restart");
        // The marker itself is attached to the first post-restart sample.
        yield return (12.0, true, true, false, ReplayExpectedState.Normal, "restart");
        for (int i = 0; i < 9; i++)
            yield return (12.0, true, false, false, ReplayExpectedState.Normal, "post-restart");
    }
}
