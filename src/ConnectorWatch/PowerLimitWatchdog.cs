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
    /// <summary>Management-limit telemetry is present, but no independent
    /// enforcement telemetry was supplied, so end-to-end enforcement is only
    /// partially verified.</summary>
    public const string PartiallyVerified = "POWER_LIMIT_PARTIALLY_VERIFIED";
    public const string NotEnforcedPending = "POWER_LIMIT_NOT_ENFORCED_PENDING";
    public const string NotEnforced = "POWER_LIMIT_NOT_ENFORCED";
    public const string EnforcementUnavailable = "POWER_LIMIT_ENFORCEMENT_UNAVAILABLE";
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
    string Identity = "",
    double? DesiredConfiguredCapWatts = null,
    double? EnforcedLimitWatts = null,
    bool EnforcementTelemetrySupported = false,
    double? ConnectorPowerWatts = null)
{
    public DateTimeOffset HostTime => HostTimestampUtc.ToUniversalTime();

    /// <summary>The configured cap is separate from the provider's reported
    /// management limit.  The old ConfiguredLimitWatts field remains the
    /// compatibility input for callers that predate this distinction.</summary>
    public double? DesiredCapWatts => DesiredConfiguredCapWatts ?? ConfiguredLimitWatts;

    /// <summary>Compatibility name for the provider-reported management
    /// limit; it is never treated as independent enforcement evidence.</summary>
    public double? ReportedManagementLimitWatts => ObservedLimitWatts;

    public bool HasFiniteDesiredCap => DesiredCapWatts is double value &&
        double.IsFinite(value) && value > 0;

    public bool HasFiniteEnforcedLimit => EnforcedLimitWatts is double value &&
        double.IsFinite(value) && value > 0;

    public bool HasFiniteConnectorPower => ConnectorPowerWatts is double value &&
        double.IsFinite(value) && value >= 0;

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
    double NearLimitMarginWatts = 5,
    double NotEnforcedToleranceWatts = 1)
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
        if (!double.IsFinite(NotEnforcedToleranceWatts) || NotEnforcedToleranceWatts < 0)
            throw new ArgumentOutOfRangeException(nameof(NotEnforcedToleranceWatts));
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
    string LastStatus = PowerLimitWatchdogStatus.BaselineLearning,
    double? LastDesiredConfiguredCapWatts = null,
    double? LastReportedManagementLimitWatts = null,
    double? LastEnforcedLimitWatts = null,
    bool LastEnforcementTelemetrySupported = false,
    double? LastConnectorPowerWatts = null,
    int PendingNotEnforcedSamples = 0,
    double? PendingNotEnforcedSinceMonotonicSeconds = null,
    double? PendingNotEnforcedSinceHostSeconds = null,
    double? PendingNotEnforcedDifferenceWatts = null,
    string? PendingNotEnforcedStatus = null)
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
        if (PendingNotEnforcedSamples < 0)
            throw new FormatException("Power-limit watchdog not-enforced pending sample count is invalid.");
        ValidateOptionalPositive(LastDesiredConfiguredCapWatts, nameof(LastDesiredConfiguredCapWatts));
        ValidateOptionalPositive(LastReportedManagementLimitWatts, nameof(LastReportedManagementLimitWatts));
        ValidateOptionalPositive(LastEnforcedLimitWatts, nameof(LastEnforcedLimitWatts));
        ValidateOptionalNonNegative(LastConnectorPowerWatts, nameof(LastConnectorPowerWatts));
        ValidateOptionalFinite(PendingNotEnforcedSinceMonotonicSeconds,
            nameof(PendingNotEnforcedSinceMonotonicSeconds));
        ValidateOptionalFinite(PendingNotEnforcedSinceHostSeconds,
            nameof(PendingNotEnforcedSinceHostSeconds));
        ValidateOptionalFinite(PendingNotEnforcedDifferenceWatts,
            nameof(PendingNotEnforcedDifferenceWatts));
        return this with
        {
            Identity = Identity.Trim(),
            BaselineLearningWatts = Learning.ToArray(),
            LastStatus = string.IsNullOrWhiteSpace(LastStatus)
                ? PowerLimitWatchdogStatus.BaselineLearning : LastStatus.Trim(),
            PendingNotEnforcedStatus = string.IsNullOrWhiteSpace(PendingNotEnforcedStatus)
                ? null : PendingNotEnforcedStatus.Trim(),
        };
    }

    static void ValidateOptionalPositive(double? value, string name)
    {
        if (value is double number && (!double.IsFinite(number) || number <= 0))
            throw new FormatException($"Power-limit watchdog {name} is invalid.");
    }

    static void ValidateOptionalNonNegative(double? value, string name)
    {
        if (value is double number && (!double.IsFinite(number) || number < 0))
            throw new FormatException($"Power-limit watchdog {name} is invalid.");
    }

    static void ValidateOptionalFinite(double? value, string name)
    {
        if (value is double number && !double.IsFinite(number))
            throw new FormatException($"Power-limit watchdog {name} is invalid.");
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
    string Detail,
    double? DesiredConfiguredCapWatts = null,
    double? ReportedManagementLimitWatts = null,
    double? EnforcedLimitWatts = null,
    bool EnforcementTelemetrySupported = false,
    double? ConnectorPowerWatts = null,
    string EnforcementStatus = PowerLimitWatchdogStatus.PartiallyVerified)
{
    [JsonIgnore] public string StatusName => Status;
    [JsonIgnore] public string BoardStatusName => BoardPowerStatus;
    [JsonIgnore] public bool ReadOnly => IsReadOnly;
    [JsonIgnore] public bool SafetyCertificationAbsent => NoSafetyCertification;
    [JsonIgnore] public double? DesiredCapWatts => DesiredConfiguredCapWatts;
    [JsonIgnore] public double? ReportedLimitWatts => ReportedManagementLimitWatts;
    [JsonIgnore] public bool EnforcementSupported => EnforcementTelemetrySupported;

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
        PendingNotEnforcedSamples = 0,
        PendingNotEnforcedSinceMonotonicSeconds = null,
        PendingNotEnforcedSinceHostSeconds = null,
        PendingNotEnforcedDifferenceWatts = null,
        PendingNotEnforcedStatus = null,
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
        {
            ClearAllPending();
            return Result(PowerLimitWatchdogStatus.TimestampInvalid, PowerLimitBoardStatus.Unavailable,
                observation, null, null, null, false, false, "Host timestamp is invalid.");
        }

        double? monotonic = monotonicSeconds is double mono && double.IsFinite(mono) && mono >= 0
            ? mono : null;
        if (monotonicSeconds.HasValue && !monotonic.HasValue)
        {
            ClearAllPending();
            return Result(PowerLimitWatchdogStatus.TimestampInvalid, PowerLimitBoardStatus.Unavailable,
                observation, null, null, null, false, false, "Monotonic timestamp is invalid.");
        }

        if (!string.IsNullOrWhiteSpace(observation.Identity) &&
            !string.Equals(observation.Identity.Trim(), identity, StringComparison.Ordinal))
        {
            ClearAllPending();
            return Result(PowerLimitWatchdogStatus.IdentityMismatch,
                BoardStatus(observation, null), observation, null, null, null, false, true,
                "Telemetry identity does not match the persisted watchdog identity.");
        }

        var previousDesired = state.LastDesiredConfiguredCapWatts;
        var previousEnforcementSupport = state.LastEnforcementTelemetrySupported;
        // Persist the channels independently, including nulls.  A restart or
        // status consumer can therefore tell that an unsupported/missing
        // channel was observed instead of accidentally inheriting an older
        // value from a previous poll.
        state = state with
        {
            LastDesiredConfiguredCapWatts = ValidPositive(observation.DesiredCapWatts),
            LastReportedManagementLimitWatts = ValidPositive(observation.ReportedManagementLimitWatts),
            LastEnforcedLimitWatts = ValidPositive(observation.EnforcedLimitWatts),
            LastEnforcementTelemetrySupported = observation.EnforcementTelemetrySupported,
            LastConnectorPowerWatts = ValidNonNegative(observation.ConnectorPowerWatts),
        };

        if (state.LastHostTimestampUtc is DateTimeOffset previousHost)
        {
            double elapsed = (host - previousHost.ToUniversalTime()).TotalSeconds;
            if (!double.IsFinite(elapsed) || elapsed < 0 || elapsed > options.MaximumGapSeconds)
            {
                ClearAllPending();
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
                ClearAllPending();
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
        {
            ClearAllPending();
            return Result(PowerLimitWatchdogStatus.Stale, BoardStatus(observation, null), observation,
                null, null, null, false, false, "Power-limit telemetry is marked stale.");
        }
        if (!observation.FreshnessVerified)
        {
            ClearAllPending();
            return Result(PowerLimitWatchdogStatus.FreshnessUnverified,
                BoardStatus(observation, null), observation, null, null, null, false, false,
                "Freshness was not independently verified by the source adapter.");
        }
        if (observation.SourceTimestampUtc is DateTimeOffset source)
        {
            double age = (host - source.ToUniversalTime()).TotalSeconds;
            if (!double.IsFinite(age) || age < 0 || age > options.MaximumGapSeconds)
            {
                ClearAllPending();
                return Result(PowerLimitWatchdogStatus.Stale, PowerLimitBoardStatus.Stale,
                    observation, null, null, null, false, true, "Source timestamp is stale or in the future.");
            }
        }
        double? desired = ValidPositive(observation.DesiredCapWatts);
        double? reported = ValidPositive(observation.ReportedManagementLimitWatts);
        if (!reported.HasValue)
        {
            ClearAllPending();
            return Result(PowerLimitWatchdogStatus.Unavailable,
                BoardStatus(observation, null), observation, null, null, null, false, true,
                "Reported management power limit is unavailable or non-finite.");
        }

        double observed = reported.Value;
        string enforcementStatus = UpdateEnforcementState(observation, desired, monotonic, host,
            previousDesired, previousEnforcementSupport);
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
                    ConfiguredBaselineWatts = desired,
                    BaselineLearningWatts = Array.Empty<double>(),
                    LastStatus = PowerLimitWatchdogStatus.Ok,
                };
                return Result(PowerLimitWatchdogStatus.Ok, BoardStatus(observation, baseline),
                    observation, baseline, 0, 0, true, true,
                    "Read-only baseline established from confirmed observations.",
                    enforcementStatus: enforcementStatus);
            }

            state = state with
            {
                BaselineLearningWatts = learning,
                ConfiguredBaselineWatts = state.ConfiguredBaselineWatts ??
                    desired,
                LastStatus = PowerLimitWatchdogStatus.BaselineLearning,
            };
            return Result(PowerLimitWatchdogStatus.BaselineLearning,
                BoardStatus(observation, null), observation, null, null, null, true, true,
                $"Collecting baseline observation {learning.Count}/{options.BaselineSamples}; no limit change is inferred.",
                enforcementStatus: enforcementStatus);
        }

        double baselineLimit = state.BaselineLimitWatts.Value;
        double? configured = desired;
        double? fromBaseline = observed - baselineLimit;
        double? fromConfigured = configured.HasValue ? observed - configured.Value : null;

        if (state.ConfiguredBaselineWatts is double configuredBaseline && configured.HasValue &&
            Math.Abs(configured.Value - configuredBaseline) > options.DriftToleranceWatts)
        {
            ClearPending();
            ClearNotEnforcedPending();
            state = state with { ConfiguredBaselineWatts = configured,
                LastStatus = PowerLimitWatchdogStatus.ConfigChanged };
            return Result(PowerLimitWatchdogStatus.ConfigChanged,
                BoardStatus(observation, baselineLimit), observation, baselineLimit,
                fromConfigured, fromBaseline, true, true,
                "Configured limit changed; re-baselining requires an explicit operator action.",
                enforcementStatus: EnforcementStatusAfterConfigurationChange(observation));
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
        if (anomaly is null && fromConfigured is null)
        {
            if (fromBaseline > options.DriftToleranceWatts) anomaly = PowerLimitWatchdogStatus.Increased;
            else if (fromBaseline < -options.DriftToleranceWatts) anomaly = PowerLimitWatchdogStatus.Decreased;
            else if (Math.Abs(fromBaseline.Value) > options.IncreaseToleranceWatts)
                anomaly = PowerLimitWatchdogStatus.Drift;
        }

        if (anomaly is null)
        {
            ClearPending();
            if (enforcementStatus is PowerLimitWatchdogStatus.NotEnforced or
                PowerLimitWatchdogStatus.NotEnforcedPending)
            {
                state = state with { LastStatus = enforcementStatus };
                return Result(enforcementStatus, BoardStatus(observation, baselineLimit),
                    observation, baselineLimit, fromConfigured, fromBaseline, true, true,
                    enforcementStatus == PowerLimitWatchdogStatus.NotEnforced
                        ? "Enforced-limit telemetry is above the desired configured cap after bounded confirmation; watchdog remains read-only."
                        : "Enforced-limit telemetry is above the desired configured cap and is pending bounded confirmation.",
                    confirmationSamples: state.PendingNotEnforcedSamples,
                    confirmationElapsedSeconds: PendingNotEnforcedElapsed(host, monotonic),
                    enforcementStatus: enforcementStatus);
            }
            if (state.Latched && state.LastStatus.EndsWith("_LATCHED", StringComparison.Ordinal))
            {
                return Result(state.LastStatus, BoardStatus(observation, baselineLimit),
                    observation, baselineLimit, fromConfigured, fromBaseline, true, true,
                    "A previously confirmed power-limit incident remains latched until explicitly acknowledged.",
                    latched: true, enforcementStatus: enforcementStatus);
            }
            state = state with { LastStatus = PowerLimitWatchdogStatus.Ok };
            return Result(PowerLimitWatchdogStatus.Ok, BoardStatus(observation, baselineLimit),
                observation, baselineLimit, fromConfigured, fromBaseline, true, true,
                "Observed power limit is within the read-only confirmation band.",
                enforcementStatus: enforcementStatus);
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
        string reportedStatus = latched ? anomaly + "_LATCHED" : anomaly;
        state = state with
        {
            PendingSamples = pendingSamples,
            PendingSinceMonotonicSeconds = pendingSinceMonotonic,
            PendingSinceHostSeconds = pendingSinceHostSeconds,
            PendingDifferenceWatts = difference,
            PendingStatus = anomaly,
            Latched = latched,
            LastStatus = reportedStatus,
        };
        string detail = confirmed
            ? "Unexpected observed power-limit change confirmed and latched; watchdog remains read-only."
            : $"Power-limit {anomaly.ToLowerInvariant()} pending confirmation ({pendingSamples}/{options.MinimumConfirmationSamples}, {elapsedPending:R}s).";
        return Result(reportedStatus, BoardStatus(observation, baselineLimit), observation,
            baselineLimit, fromConfigured, fromBaseline, true, true, detail,
            pendingSamples, elapsedPending, latched, enforcementStatus);
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

    void ClearNotEnforcedPending() => state = state with
    {
        PendingNotEnforcedSamples = 0,
        PendingNotEnforcedSinceMonotonicSeconds = null,
        PendingNotEnforcedSinceHostSeconds = null,
        PendingNotEnforcedDifferenceWatts = null,
        PendingNotEnforcedStatus = null,
    };

    void ClearAllPending()
    {
        ClearPending();
        ClearNotEnforcedPending();
    }

    string UpdateEnforcementState(PowerLimitObservation observation,
        double? desired, double? monotonic, DateTimeOffset host,
        double? previousDesired, bool previousSupport)
    {
        if (!observation.EnforcementTelemetrySupported)
        {
            ClearNotEnforcedPending();
            return PowerLimitWatchdogStatus.PartiallyVerified;
        }
        if (!desired.HasValue || !observation.HasFiniteEnforcedLimit)
        {
            ClearNotEnforcedPending();
            return PowerLimitWatchdogStatus.EnforcementUnavailable;
        }

        // A cap or support-mode change starts a new bounded observation.  The
        // previous pending count must never be carried across that boundary.
        bool capChanged = previousDesired.HasValue &&
            Math.Abs(previousDesired.Value - desired.Value) > options.DriftToleranceWatts;
        if (!previousSupport || capChanged)
            ClearNotEnforcedPending();

        double difference = observation.EnforcedLimitWatts!.Value - desired.Value;
        if (difference <= options.NotEnforcedToleranceWatts)
        {
            ClearNotEnforcedPending();
            return PowerLimitWatchdogStatus.Ok;
        }

        bool samePending = state.PendingNotEnforcedStatus ==
            PowerLimitWatchdogStatus.NotEnforcedPending;
        int samples = samePending ? state.PendingNotEnforcedSamples + 1 : 1;
        double? sinceHost = samePending && state.PendingNotEnforcedSinceHostSeconds.HasValue
            ? state.PendingNotEnforcedSinceHostSeconds
            : host.ToUnixTimeMilliseconds() / 1000d;
        double? sinceMonotonic = samePending && state.PendingNotEnforcedSinceMonotonicSeconds.HasValue
            ? state.PendingNotEnforcedSinceMonotonicSeconds : monotonic;
        double elapsed = PendingElapsed(host, monotonic, sinceHost, sinceMonotonic);
        bool confirmed = samples >= options.MinimumConfirmationSamples &&
            (options.ConfirmationSeconds <= 0 || elapsed >= options.ConfirmationSeconds);
        var status = confirmed ? PowerLimitWatchdogStatus.NotEnforced
            : PowerLimitWatchdogStatus.NotEnforcedPending;
        state = state with
        {
            PendingNotEnforcedSamples = samples,
            PendingNotEnforcedSinceMonotonicSeconds = sinceMonotonic,
            PendingNotEnforcedSinceHostSeconds = sinceHost,
            PendingNotEnforcedDifferenceWatts = difference,
            PendingNotEnforcedStatus = PowerLimitWatchdogStatus.NotEnforcedPending,
        };
        return status;
    }

    string EnforcementStatusAfterConfigurationChange(PowerLimitObservation observation) =>
        !observation.EnforcementTelemetrySupported
            ? PowerLimitWatchdogStatus.PartiallyVerified
            : observation.HasFiniteDesiredCap && observation.HasFiniteEnforcedLimit
                ? PowerLimitWatchdogStatus.Ok
                : PowerLimitWatchdogStatus.EnforcementUnavailable;

    double PendingNotEnforcedElapsed(DateTimeOffset host, double? monotonic) =>
        PendingElapsed(host, monotonic, state.PendingNotEnforcedSinceHostSeconds,
            state.PendingNotEnforcedSinceMonotonicSeconds);

    static double PendingElapsed(DateTimeOffset host, double? monotonic,
        double? sinceHost, double? sinceMonotonic)
    {
        if (monotonic is double current && sinceMonotonic is double start)
            return Math.Max(0, current - start);
        var hostSeconds = host.ToUnixTimeMilliseconds() / 1000d;
        return Math.Max(0, hostSeconds - (sinceHost ?? hostSeconds));
    }

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
        bool? latched = null, string? enforcementStatus = null)
    {
        string enforcement = enforcementStatus ?? EnforcementStatusForObservation(observation);
        string effectiveStatus = status;
        // A verified management-limit read without an independent enforced
        // limit is useful, but cannot claim end-to-end enforcement.  Keep the
        // more specific stale/unverified/change statuses intact and surface
        // the partial state for the ordinary healthy path.
        if ((status is PowerLimitWatchdogStatus.Ok or PowerLimitWatchdogStatus.BaselineLearning) &&
            enforcement == PowerLimitWatchdogStatus.PartiallyVerified &&
            observation.FreshnessVerified && observation.HasFiniteObservedLimit)
            effectiveStatus = PowerLimitWatchdogStatus.PartiallyVerified;
        return new(
        effectiveStatus,
        boardStatus,
        IsReadOnly: true,
        NoSafetyCertification: true,
        limitAvailable,
        observation.HasFiniteBoardPower,
        observation.IsFresh,
        freshnessVerified,
        observation.HostTime,
        observation.SourceTimestampUtc?.ToUniversalTime(),
        ValidPositive(observation.DesiredCapWatts),
        ValidPositive(observation.ReportedManagementLimitWatts),
        observation.HasFiniteBoardPower ? observation.BoardPowerWatts : null,
        baseline,
        differenceFromConfigured,
        differenceFromBaseline,
        confirmationSamples ?? state.PendingSamples,
        confirmationElapsedSeconds,
        latched ?? state.Latched,
        detail,
        DesiredConfiguredCapWatts: ValidPositive(observation.DesiredCapWatts),
        ReportedManagementLimitWatts: ValidPositive(observation.ReportedManagementLimitWatts),
        EnforcedLimitWatts: observation.EnforcementTelemetrySupported &&
            observation.HasFiniteEnforcedLimit ? observation.EnforcedLimitWatts : null,
        EnforcementTelemetrySupported: observation.EnforcementTelemetrySupported,
        ConnectorPowerWatts: ValidNonNegative(observation.ConnectorPowerWatts),
        EnforcementStatus: enforcement);
    }

    string EnforcementStatusForObservation(PowerLimitObservation observation) =>
        !observation.EnforcementTelemetrySupported
            ? PowerLimitWatchdogStatus.PartiallyVerified
            : observation.HasFiniteDesiredCap && observation.HasFiniteEnforcedLimit
                ? PowerLimitWatchdogStatus.Ok
                : PowerLimitWatchdogStatus.EnforcementUnavailable;

    static double? ValidPositive(double? value) => value is double number &&
        double.IsFinite(number) && number > 0 ? number : null;

    static double? ValidNonNegative(double? value) => value is double number &&
        double.IsFinite(number) && number >= 0 ? number : null;

    static double Median(IEnumerable<double> values)
    {
        var sorted = values.Order().ToArray();
        if (sorted.Length == 0) throw new ArgumentException("Median requires values.", nameof(values));
        int middle = sorted.Length / 2;
        return sorted.Length % 2 == 0 ? (sorted[middle - 1] + sorted[middle]) / 2 : sorted[middle];
    }
}
