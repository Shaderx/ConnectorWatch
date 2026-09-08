using System.Text.Json;
using System.Text.Json.Serialization;

namespace ConnectorWatch;

/// <summary>
/// Read-only power-limit watchdog statuses.  These values describe telemetry
/// and configuration observations; none of them is a safety certification.
/// </summary>
public static class PowerLimitWatchdogStatus
{
    public const string BaselineLearning = "POWER_LIMIT_BASELINE_LEARNING";
    public const string Ok = "POWER_LIMIT_OK";
    public const string Increased = "POWER_LIMIT_INCREASED";
    public const string IncreasedLatched = "POWER_LIMIT_INCREASED_LATCHED";
    public const string Drift = "POWER_LIMIT_DRIFT";
    public const string Decreased = "POWER_LIMIT_DECREASED";
    public const string ConfigChanged = "POWER_LIMIT_CONFIGURATION_CHANGED";
    public const string Unavailable = "POWER_LIMIT_UNAVAILABLE";
    public const string Stale = "POWER_LIMIT_STALE";
    public const string FreshnessUnverified = "POWER_LIMIT_FRESHNESS_UNVERIFIED";
    public const string TimestampInvalid = "POWER_LIMIT_TIMESTAMP_INVALID";
    public const string TimeDiscontinuity = "POWER_LIMIT_TIME_DISCONTINUITY";
    public const string IdentityMismatch = "POWER_LIMIT_IDENTITY_MISMATCH";
}

public static class PowerLimitBoardStatus
{
    public const string Available = "BOARD_POWER_AVAILABLE";
    public const string Unavailable = "BOARD_POWER_UNAVAILABLE";
    public const string Stale = "BOARD_POWER_STALE";
    public const string NearLimit = "BOARD_POWER_NEAR_LIMIT";
    public const string OverObservedLimit = "BOARD_POWER_OVER_OBSERVED_LIMIT";
}

/// <summary>One observation from a read-only telemetry provider.</summary>
public sealed record PowerLimitObservation(
    DateTimeOffset HostTimestampUtc,
    double? ConfiguredLimitWatts,
    double? ObservedLimitWatts,
    double? BoardPowerWatts,
    bool IsFresh = true,
    DateTimeOffset? SourceTimestampUtc = null,
    bool FreshnessVerified = false,
    string Source = "",
    string Identity = "")
{
    public DateTimeOffset HostTime => HostTimestampUtc.ToUniversalTime();

    public bool HasFiniteConfiguredLimit => ConfiguredLimitWatts is double value &&
        double.IsFinite(value) && value > 0;

    public bool HasFiniteObservedLimit => ObservedLimitWatts is double value &&
        double.IsFinite(value) && value > 0;

    public bool HasFiniteBoardPower => BoardPowerWatts is double value &&
        double.IsFinite(value) && value >= 0;
}

/// <summary>
/// Bounds for a watchdog.  They are telemetry confirmation settings, not
/// electrical or thermal safety limits.
/// </summary>
public sealed record PowerLimitWatchdogOptions(
    double IncreaseToleranceWatts = 1,
    double DriftToleranceWatts = 2,
    int MinimumConfirmationSamples = 3,
    double ConfirmationSeconds = 0,
    int BaselineSamples = 3,
    double MaximumGapSeconds = 30,
    double NearLimitMarginWatts = 5)
{
    public void Validate()
    {
        if (!double.IsFinite(IncreaseToleranceWatts) || IncreaseToleranceWatts < 0)
            throw new ArgumentOutOfRangeException(nameof(IncreaseToleranceWatts));
        if (!double.IsFinite(DriftToleranceWatts) || DriftToleranceWatts < IncreaseToleranceWatts)
            throw new ArgumentOutOfRangeException(nameof(DriftToleranceWatts));
        if (MinimumConfirmationSamples < 1)
            throw new ArgumentOutOfRangeException(nameof(MinimumConfirmationSamples));
        if (!double.IsFinite(ConfirmationSeconds) || ConfirmationSeconds < 0)
            throw new ArgumentOutOfRangeException(nameof(ConfirmationSeconds));
        if (BaselineSamples < 1)
            throw new ArgumentOutOfRangeException(nameof(BaselineSamples));
        if (!double.IsFinite(MaximumGapSeconds) || MaximumGapSeconds <= 0)
            throw new ArgumentOutOfRangeException(nameof(MaximumGapSeconds));
        if (!double.IsFinite(NearLimitMarginWatts) || NearLimitMarginWatts < 0)
            throw new ArgumentOutOfRangeException(nameof(NearLimitMarginWatts));
    }
}

/// <summary>
/// The persisted portion of watchdog state.  Baseline and pending incident
/// data are intentionally serializable so a daemon restart cannot silently
/// forget an observed limit increase.  The watchdog never contains a setter
/// or native call for changing a limit.
/// </summary>
public sealed record PowerLimitWatchdogState(
    int SchemaVersion = 1,
    string Identity = "",
    double? BaselineLimitWatts = null,
    double? ConfiguredBaselineWatts = null,
    IReadOnlyList<double>? BaselineLearningWatts = null,
    int PendingSamples = 0,
    double? PendingSinceMonotonicSeconds = null,
    double? PendingSinceHostSeconds = null,
    double? PendingDifferenceWatts = null,
    string? PendingStatus = null,
    bool Latched = false,
    DateTimeOffset? LastHostTimestampUtc = null,
    DateTimeOffset? LastSourceTimestampUtc = null,
    double? LastMonotonicSeconds = null,
    string LastStatus = PowerLimitWatchdogStatus.BaselineLearning)
{
    [JsonIgnore]
    public IReadOnlyList<double> Learning => BaselineLearningWatts ?? Array.Empty<double>();

    public PowerLimitWatchdogState Normalize()
    {
        if (SchemaVersion != 1)
            throw new NotSupportedException($"Unsupported power-limit watchdog schema {SchemaVersion}.");
        if (string.IsNullOrWhiteSpace(Identity))
            throw new FormatException("Power-limit watchdog state requires an identity.");
        if (Learning.Any(value => !double.IsFinite(value) || value <= 0))
            throw new FormatException("Power-limit watchdog baseline learning contains an invalid value.");
        if (PendingSamples < 0)
            throw new FormatException("Power-limit watchdog pending sample count is invalid.");
        return this with
        {
            Identity = Identity.Trim(),
            BaselineLearningWatts = Learning.ToArray(),
            LastStatus = string.IsNullOrWhiteSpace(LastStatus)
                ? PowerLimitWatchdogStatus.BaselineLearning : LastStatus.Trim(),
        };
    }
}

/// <summary>Machine-readable result of one watchdog observation.</summary>
public sealed record PowerLimitWatchdogResult(
    string Status,
    string BoardPowerStatus,
    bool IsReadOnly,
    bool NoSafetyCertification,
    bool LimitAvailable,
    bool BoardPowerAvailable,
    bool IsFresh,
    bool FreshnessVerified,
    DateTimeOffset HostTimestampUtc,
    DateTimeOffset? SourceTimestampUtc,
    double? ConfiguredLimitWatts,
    double? ObservedLimitWatts,
    double? BoardPowerWatts,
    double? BaselineLimitWatts,
    double? DifferenceFromConfiguredWatts,
    double? DifferenceFromBaselineWatts,
    int ConfirmationSamples,
    double? ConfirmationElapsedSeconds,
    bool IncidentLatched,
    string Detail)
{
    [JsonIgnore] public string StatusName => Status;
    [JsonIgnore] public string BoardStatusName => BoardPowerStatus;
    [JsonIgnore] public bool ReadOnly => IsReadOnly;
    [JsonIgnore] public bool SafetyCertificationAbsent => NoSafetyCertification;

    public string ToJson(bool indented = true) => JsonSerializer.Serialize(this,
        PowerLimitWatchdogJson.Options(indented));
}

public static class PowerLimitWatchdogJson
{
    public static JsonSerializerOptions Options(bool indented = true) => new()
    {
        WriteIndented = indented,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
    };
}

/// <summary>
/// Stateful, monotonic/time-aware, read-only power-limit monitor.  A limit is
/// only reported after the configured sample/time confirmation; a confirmed
/// increase is latched until an explicit operator acknowledgement.  The
/// implementation intentionally exposes no write path to NVML/NVAPI.
/// </summary>
public sealed class PowerLimitWatchdog
{
    public const int CurrentSchemaVersion = 1;
    public const bool WritesPowerLimit = false;

    readonly PowerLimitWatchdogOptions options;
    readonly string identity;
    PowerLimitWatchdogState state;

    public PowerLimitWatchdog(PowerLimitWatchdogOptions? options = null,
        string identity = "unspecified", PowerLimitWatchdogState? persisted = null)
    {
        this.options = options ?? new PowerLimitWatchdogOptions();
        this.options.Validate();
        if (string.IsNullOrWhiteSpace(identity)) throw new ArgumentException(
            "A watchdog identity is required.", nameof(identity));
        this.identity = identity.Trim();
        state = (persisted ?? new PowerLimitWatchdogState(Identity: this.identity))
            .Normalize();
        if (!string.Equals(state.Identity, this.identity, StringComparison.Ordinal))
            throw new ArgumentException("Persisted watchdog state identity does not match.", nameof(persisted));
    }

    public PowerLimitWatchdogOptions Options => options;
    public PowerLimitWatchdogState State => state;
    public string Identity => identity;

    /// <summary>Serialize only state; it contains no command or write capability.</summary>
    public string SerializeState() => JsonSerializer.Serialize(state,
        PowerLimitWatchdogJson.Options(true));

    public static PowerLimitWatchdog Restore(string json,
        PowerLimitWatchdogOptions? options = null, string? identity = null)
    {
        if (string.IsNullOrWhiteSpace(json)) throw new FormatException("Watchdog state is empty.");
        var saved = JsonSerializer.Deserialize<PowerLimitWatchdogState>(json,
            PowerLimitWatchdogJson.Options(false))?.Normalize()
            ?? throw new FormatException("Watchdog state is invalid.");
        var effectiveIdentity = string.IsNullOrWhiteSpace(identity) ? saved.Identity : identity.Trim();
        return new PowerLimitWatchdog(options, effectiveIdentity, saved);
    }

    /// <summary>Acknowledgement is explicit and does not change any hardware setting.</summary>
    public void AcknowledgeIncident() => state = state with
    {
        Latched = false,
        PendingSamples = 0,
        PendingSinceMonotonicSeconds = null,
        PendingSinceHostSeconds = null,
        PendingDifferenceWatts = null,
        PendingStatus = null,
        LastStatus = PowerLimitWatchdogStatus.Ok,
    };

    /// <summary>
    /// Explicitly discard the learned baseline after an operator changes
    /// configuration or hardware.  It still does not write a power limit.
    /// </summary>
    public void ArchiveBaseline() => state = new PowerLimitWatchdogState(
        Identity: identity, LastStatus: PowerLimitWatchdogStatus.BaselineLearning);

    public PowerLimitWatchdogResult Observe(PowerLimitObservation observation,
        double? monotonicSeconds = null)
    {
        if (observation is null) throw new ArgumentNullException(nameof(observation));
        var host = observation.HostTime;
        if (host == default)
            return Result(PowerLimitWatchdogStatus.TimestampInvalid, PowerLimitBoardStatus.Unavailable,
                observation, null, null, null, false, false, "Host timestamp is invalid.");

        double? monotonic = monotonicSeconds is double mono && double.IsFinite(mono) && mono >= 0
            ? mono : null;
        if (monotonicSeconds.HasValue && !monotonic.HasValue)
            return Result(PowerLimitWatchdogStatus.TimestampInvalid, PowerLimitBoardStatus.Unavailable,
                observation, null, null, null, false, false, "Monotonic timestamp is invalid.");

        if (state.LastHostTimestampUtc is DateTimeOffset previousHost)
        {
            double elapsed = (host - previousHost.ToUniversalTime()).TotalSeconds;
            if (!double.IsFinite(elapsed) || elapsed < 0 || elapsed > options.MaximumGapSeconds)
            {
                ClearPending();
                state = state with { LastHostTimestampUtc = host,
                    LastSourceTimestampUtc = observation.SourceTimestampUtc?.ToUniversalTime(),
                    LastMonotonicSeconds = monotonic,
                    LastStatus = PowerLimitWatchdogStatus.TimeDiscontinuity };
                return Result(PowerLimitWatchdogStatus.TimeDiscontinuity,
                    PowerLimitBoardStatus.Unavailable, observation, null, null, null, false, false,
                    elapsed < 0 ? "Host time moved backwards; confirmation was reset."
                        : "Host time gap exceeded the confirmation bound; confirmation was reset.");
            }
        }

        if (monotonic is double currentMonotonic &&
            state.LastMonotonicSeconds is double previousMonotonic)
        {
            double elapsedMonotonic = currentMonotonic - previousMonotonic;
            if (!double.IsFinite(elapsedMonotonic) || elapsedMonotonic < 0 ||
                elapsedMonotonic > options.MaximumGapSeconds)
            {
                ClearPending();
                state = state with
                {
                    LastHostTimestampUtc = host,
                    LastSourceTimestampUtc = observation.SourceTimestampUtc?.ToUniversalTime(),
                    LastMonotonicSeconds = currentMonotonic,
                    LastStatus = PowerLimitWatchdogStatus.TimeDiscontinuity,
                };
                return Result(PowerLimitWatchdogStatus.TimeDiscontinuity,
                    PowerLimitBoardStatus.Unavailable, observation, null, null, null, false, false,
                    "Monotonic time moved backwards or exceeded the confirmation bound; confirmation was reset.");
            }
        }

        state = state with
        {
            LastHostTimestampUtc = host,
            LastSourceTimestampUtc = observation.SourceTimestampUtc?.ToUniversalTime(),
            LastMonotonicSeconds = monotonic,
        };

        if (!observation.IsFresh)
            return Result(PowerLimitWatchdogStatus.Stale, BoardStatus(observation, null), observation,
                null, null, null, false, false, "Power-limit telemetry is marked stale.");
        if (!observation.FreshnessVerified)
            return Result(PowerLimitWatchdogStatus.FreshnessUnverified,
                BoardStatus(observation, null), observation, null, null, null, false, false,
                "Freshness was not independently verified by the source adapter.");
        if (observation.SourceTimestampUtc is DateTimeOffset source)
        {
            double age = (host - source.ToUniversalTime()).TotalSeconds;
            if (!double.IsFinite(age) || age < 0 || age > options.MaximumGapSeconds)
                return Result(PowerLimitWatchdogStatus.Stale, PowerLimitBoardStatus.Stale,
                    observation, null, null, null, false, true, "Source timestamp is stale or in the future.");
        }
        if (!string.IsNullOrWhiteSpace(observation.Identity) &&
            !string.Equals(observation.Identity.Trim(), identity, StringComparison.Ordinal))
            return Result(PowerLimitWatchdogStatus.IdentityMismatch,
                BoardStatus(observation, null), observation, null, null, null, false, true,
                "Telemetry identity does not match the persisted watchdog identity.");
        if (!observation.HasFiniteObservedLimit)
            return Result(PowerLimitWatchdogStatus.Unavailable,
                BoardStatus(observation, null), observation, null, null, null, false, true,
                "Observed power limit is unavailable or non-finite.");

        double observed = observation.ObservedLimitWatts!.Value;
        if (state.BaselineLimitWatts is null)
        {
            var learning = state.Learning.ToList();
            learning.Add(observed);
            if (learning.Count >= options.BaselineSamples)
            {
                double baseline = Median(learning);
                state = state with
                {
                    BaselineLimitWatts = baseline,
                    ConfiguredBaselineWatts = ValidPositive(observation.ConfiguredLimitWatts),
                    BaselineLearningWatts = Array.Empty<double>(),
                    LastStatus = PowerLimitWatchdogStatus.Ok,
                };
                return Result(PowerLimitWatchdogStatus.Ok, BoardStatus(observation, baseline),
                    observation, baseline, 0, 0, true, true,
                    "Read-only baseline established from confirmed observations.");
            }

            state = state with
            {
                BaselineLearningWatts = learning,
                ConfiguredBaselineWatts = state.ConfiguredBaselineWatts ??
                    ValidPositive(observation.ConfiguredLimitWatts),
                LastStatus = PowerLimitWatchdogStatus.BaselineLearning,
            };
            return Result(PowerLimitWatchdogStatus.BaselineLearning,
                BoardStatus(observation, null), observation, null, null, null, true, true,
                $"Collecting baseline observation {learning.Count}/{options.BaselineSamples}; no limit change is inferred.");
        }

        double baselineLimit = state.BaselineLimitWatts.Value;
        double? configured = ValidPositive(observation.ConfiguredLimitWatts);
        double? fromBaseline = observed - baselineLimit;
        double? fromConfigured = configured.HasValue ? observed - configured.Value : null;

        if (state.ConfiguredBaselineWatts is double configuredBaseline && configured.HasValue &&
            Math.Abs(configured.Value - configuredBaseline) > options.DriftToleranceWatts)
        {
            ClearPending();
            state = state with { ConfiguredBaselineWatts = configured,
                LastStatus = PowerLimitWatchdogStatus.ConfigChanged };
            return Result(PowerLimitWatchdogStatus.ConfigChanged,
                BoardStatus(observation, baselineLimit), observation, baselineLimit,
                fromConfigured, fromBaseline, true, true,
                "Configured limit changed; re-baselining requires an explicit operator action.");
        }

        string? anomaly = null;
        double difference = fromConfigured ?? fromBaseline.Value;
        if (fromConfigured is double configuredDifference)
        {
            if (configuredDifference > options.DriftToleranceWatts)
                anomaly = PowerLimitWatchdogStatus.Increased;
            else if (configuredDifference < -options.DriftToleranceWatts)
                anomaly = PowerLimitWatchdogStatus.Decreased;
            else if (Math.Abs(configuredDifference) > options.IncreaseToleranceWatts)
                anomaly = PowerLimitWatchdogStatus.Drift;
        }
        if (anomaly is null)
        {
            if (fromBaseline > options.DriftToleranceWatts) anomaly = PowerLimitWatchdogStatus.Increased;
            else if (fromBaseline < -options.DriftToleranceWatts) anomaly = PowerLimitWatchdogStatus.Decreased;
            else if (Math.Abs(fromBaseline.Value) > options.IncreaseToleranceWatts)
                anomaly = PowerLimitWatchdogStatus.Drift;
        }

        if (anomaly is null)
        {
            ClearPending();
            if (state.Latched && state.LastStatus.EndsWith("_LATCHED", StringComparison.Ordinal))
            {
                return Result(state.LastStatus, BoardStatus(observation, baselineLimit),
                    observation, baselineLimit, fromConfigured, fromBaseline, true, true,
                    "A previously confirmed power-limit incident remains latched until explicitly acknowledged.",
                    latched: true);
            }
            state = state with { LastStatus = PowerLimitWatchdogStatus.Ok };
            return Result(PowerLimitWatchdogStatus.Ok, BoardStatus(observation, baselineLimit),
                observation, baselineLimit, fromConfigured, fromBaseline, true, true,
                "Observed power limit is within the read-only confirmation band.");
        }

        bool samePending = string.Equals(state.PendingStatus, anomaly, StringComparison.Ordinal);
        int pendingSamples = samePending ? state.PendingSamples + 1 : 1;
        double? pendingSinceHostSeconds = samePending && state.PendingSinceHostSeconds.HasValue
            ? state.PendingSinceHostSeconds : host.ToUnixTimeMilliseconds() / 1000d;
        double? pendingSinceMonotonic = samePending && state.PendingSinceMonotonicSeconds.HasValue
            ? state.PendingSinceMonotonicSeconds : monotonic;
        double elapsedPending = monotonic is double currentMono &&
            pendingSinceMonotonic is double startMono
            ? Math.Max(0, currentMono - startMono)
            : Math.Max(0, host.ToUnixTimeMilliseconds() / 1000d -
                (pendingSinceHostSeconds ?? host.ToUnixTimeMilliseconds() / 1000d));
        bool timeConfirmed = options.ConfirmationSeconds <= 0 ||
            elapsedPending >= options.ConfirmationSeconds;
        bool sampleConfirmed = pendingSamples >= options.MinimumConfirmationSamples;
        bool confirmed = timeConfirmed && sampleConfirmed;
        bool latched = state.Latched || confirmed;
        string reported = latched ? anomaly + "_LATCHED" : anomaly;
        state = state with
        {
            PendingSamples = pendingSamples,
            PendingSinceMonotonicSeconds = pendingSinceMonotonic,
            PendingSinceHostSeconds = pendingSinceHostSeconds,
            PendingDifferenceWatts = difference,
            PendingStatus = anomaly,
            Latched = latched,
            LastStatus = reported,
        };
        string detail = confirmed
            ? "Unexpected observed power-limit change confirmed and latched; watchdog remains read-only."
            : $"Power-limit {anomaly.ToLowerInvariant()} pending confirmation ({pendingSamples}/{options.MinimumConfirmationSamples}, {elapsedPending:R}s).";
        return Result(reported, BoardStatus(observation, baselineLimit), observation,
            baselineLimit, fromConfigured, fromBaseline, true, true, detail,
            pendingSamples, elapsedPending, latched);
    }

    /// <summary>
    /// A watchdog has no write operation by design.  This guard is useful to
    /// callers wiring permissions or dependency injection: an attempted
    /// limit mutation fails closed instead of being silently ignored.
    /// </summary>
    public static void RejectLimitMutation() => throw new NotSupportedException(
        "PowerLimitWatchdog is read-only and never changes a GPU power limit.");

    void ClearPending() => state = state with
    {
        PendingSamples = 0,
        PendingSinceMonotonicSeconds = null,
        PendingSinceHostSeconds = null,
        PendingDifferenceWatts = null,
        PendingStatus = null,
    };

    string BoardStatus(PowerLimitObservation observation, double? limit)
    {
        if (!observation.HasFiniteBoardPower) return PowerLimitBoardStatus.Unavailable;
        if (!observation.IsFresh) return PowerLimitBoardStatus.Stale;
        if (limit is double value)
        {
            if (observation.BoardPowerWatts!.Value > value)
                return PowerLimitBoardStatus.OverObservedLimit;
            if (observation.BoardPowerWatts.Value >= value - options.NearLimitMarginWatts)
                return PowerLimitBoardStatus.NearLimit;
        }
        return PowerLimitBoardStatus.Available;
    }

    PowerLimitWatchdogResult Result(string status, string boardStatus,
        PowerLimitObservation observation, double? baseline,
        double? differenceFromConfigured, double? differenceFromBaseline,
        bool limitAvailable, bool freshnessVerified, string detail,
        int? confirmationSamples = null, double? confirmationElapsedSeconds = null,
        bool? latched = null) => new(
        status,
        boardStatus,
        IsReadOnly: true,
        NoSafetyCertification: true,
        limitAvailable,
        observation.HasFiniteBoardPower,
        observation.IsFresh,
        freshnessVerified,
        observation.HostTime,
        observation.SourceTimestampUtc?.ToUniversalTime(),
        ValidPositive(observation.ConfiguredLimitWatts),
        ValidPositive(observation.ObservedLimitWatts),
        observation.HasFiniteBoardPower ? observation.BoardPowerWatts : null,
        baseline,
        differenceFromConfigured,
        differenceFromBaseline,
        confirmationSamples ?? state.PendingSamples,
        confirmationElapsedSeconds,
        latched ?? state.Latched,
        detail);

    static double? ValidPositive(double? value) => value is double number &&
        double.IsFinite(number) && number > 0 ? number : null;

    static double Median(IEnumerable<double> values)
    {
        var sorted = values.Order().ToArray();
        if (sorted.Length == 0) throw new ArgumentException("Median requires values.", nameof(values));
        int middle = sorted.Length / 2;
        return sorted.Length % 2 == 0 ? (sorted[middle - 1] + sorted[middle]) / 2 : sorted[middle];
    }
}
