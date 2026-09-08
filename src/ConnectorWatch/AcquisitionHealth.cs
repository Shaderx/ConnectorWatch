using System.Text.Json.Serialization;

namespace ConnectorWatch;

/// <summary>Acquisition states are intentionally more specific than a single
/// "voltage unavailable" string.  They describe what can and cannot be
/// trusted, not whether an alert is active.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AcquisitionHealthStatus
{
    HEALTHY,
    STALE,
    SOURCE_UNAVAILABLE,
    TIMESTAMP_INVALID,
    POWER_UNAVAILABLE,
    NATIVE_FAILURE,
    SENSOR_UNCHARACTERIZED,
}

public static class AcquisitionHealthStatusExtensions
{
    public static string WireName(this AcquisitionHealthStatus status) => status switch
    {
        AcquisitionHealthStatus.HEALTHY => "HEALTHY",
        AcquisitionHealthStatus.STALE => "STALE",
        AcquisitionHealthStatus.SOURCE_UNAVAILABLE => "SOURCE_UNAVAILABLE",
        AcquisitionHealthStatus.TIMESTAMP_INVALID => "TIMESTAMP_INVALID",
        AcquisitionHealthStatus.POWER_UNAVAILABLE => "POWER_UNAVAILABLE",
        AcquisitionHealthStatus.NATIVE_FAILURE => "NATIVE_FAILURE",
        AcquisitionHealthStatus.SENSOR_UNCHARACTERIZED => "SENSOR_UNCHARACTERIZED",
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, "Unknown acquisition status."),
    };

    public static bool TryParse(string? value, out AcquisitionHealthStatus status)
    {
        status = default;
        if (string.IsNullOrWhiteSpace(value)) return false;
        var normalized = value.Trim().Replace('-', '_').Replace(' ', '_').ToUpperInvariant();
        return normalized switch
        {
            "HEALTHY" => Set(AcquisitionHealthStatus.HEALTHY, out status),
            "STALE" => Set(AcquisitionHealthStatus.STALE, out status),
            "SOURCE_UNAVAILABLE" => Set(AcquisitionHealthStatus.SOURCE_UNAVAILABLE, out status),
            "TIMESTAMP_INVALID" => Set(AcquisitionHealthStatus.TIMESTAMP_INVALID, out status),
            "POWER_UNAVAILABLE" => Set(AcquisitionHealthStatus.POWER_UNAVAILABLE, out status),
            "NATIVE_FAILURE" => Set(AcquisitionHealthStatus.NATIVE_FAILURE, out status),
            "SENSOR_UNCHARACTERIZED" => Set(AcquisitionHealthStatus.SENSOR_UNCHARACTERIZED, out status),
            _ => false,
        };
    }

    static bool Set(AcquisitionHealthStatus value, out AcquisitionHealthStatus result)
    {
        result = value;
        return true;
    }
}

/// <summary>Reasons that a detector has not produced an analyzable result.
/// They are separate from acquisition health because a live, useful coarse
/// monitor may coexist with an unavailable trend detector.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum DetectorAvailabilityReason
{
    AVAILABLE,
    LEARNING,
    SETTLING,
    SOURCE_GAP,
    STALE,
    UNSUPPORTED_LOAD,
    INSUFFICIENT_VARIATION,
    REFERENCE_UNAVAILABLE,
    UNKNOWN_LOAD,
    MONITOR_UNAVAILABLE,
    NATIVE_FAILURE,
    POWER_UNAVAILABLE,
}

public static class DetectorAvailabilityReasonExtensions
{
    public static string WireName(this DetectorAvailabilityReason reason) => reason switch
    {
        DetectorAvailabilityReason.AVAILABLE => "AVAILABLE",
        DetectorAvailabilityReason.LEARNING => "LEARNING",
        DetectorAvailabilityReason.SETTLING => "SETTLING",
        DetectorAvailabilityReason.SOURCE_GAP => "SOURCE_GAP",
        DetectorAvailabilityReason.STALE => "STALE",
        DetectorAvailabilityReason.UNSUPPORTED_LOAD => "UNSUPPORTED_LOAD",
        DetectorAvailabilityReason.INSUFFICIENT_VARIATION => "INSUFFICIENT_VARIATION",
        DetectorAvailabilityReason.REFERENCE_UNAVAILABLE => "REFERENCE_UNAVAILABLE",
        DetectorAvailabilityReason.UNKNOWN_LOAD => "UNKNOWN_LOAD",
        DetectorAvailabilityReason.MONITOR_UNAVAILABLE => "MONITOR_UNAVAILABLE",
        DetectorAvailabilityReason.NATIVE_FAILURE => "NATIVE_FAILURE",
        DetectorAvailabilityReason.POWER_UNAVAILABLE => "POWER_UNAVAILABLE",
        _ => throw new ArgumentOutOfRangeException(nameof(reason), reason, "Unknown detector reason."),
    };

    public static DetectorAvailabilityReason FromDetectorStatus(string? status) =>
        status?.Trim().ToUpperInvariant() switch
        {
            "NO_SHIFT_DETECTED" or "ANALYZED" => DetectorAvailabilityReason.AVAILABLE,
            "LEARNING_REFERENCE" or "LEARNING" => DetectorAvailabilityReason.LEARNING,
            "LOAD_SETTLING" or "SETTLING" => DetectorAvailabilityReason.SETTLING,
            "VOLTAGE_UNAVAILABLE" or "SOURCE_GAP" => DetectorAvailabilityReason.SOURCE_GAP,
            "WAITING_FOR_FRESH_VOLTAGE" or "STALE" => DetectorAvailabilityReason.STALE,
            "ANALYSIS_LOAD_UNAVAILABLE" or "UNSUPPORTED_LOAD" or "OUTSIDE_ANALYSIS_RANGE" => DetectorAvailabilityReason.UNSUPPORTED_LOAD,
            "INSUFFICIENT_VARIATION" => DetectorAvailabilityReason.INSUFFICIENT_VARIATION,
            "REFERENCE_UNAVAILABLE" or "WINDOW_WARMUP" => DetectorAvailabilityReason.REFERENCE_UNAVAILABLE,
            "NATIVE_FAILURE" => DetectorAvailabilityReason.NATIVE_FAILURE,
            "POWER_UNAVAILABLE" => DetectorAvailabilityReason.POWER_UNAVAILABLE,
            _ => DetectorAvailabilityReason.UNKNOWN_LOAD,
        };
}

public sealed record DetectorAvailability(
    [property: JsonPropertyName("detector")] string Detector,
    [property: JsonPropertyName("available")] bool Available,
    [property: JsonPropertyName("reason")] DetectorAvailabilityReason Reason,
    [property: JsonPropertyName("detail")] string Detail,
    [property: JsonPropertyName("updated_at_utc")] DateTimeOffset UpdatedAtUtc,
    [property: JsonPropertyName("eligible_samples")] long EligibleSamples,
    [property: JsonPropertyName("target_samples")] long TargetSamples)
{
    public bool IsAvailable => Available;
    public string ReasonName => Reason.WireName();

    public static DetectorAvailability Ready(string detector,
        DateTimeOffset updatedAtUtc, long eligibleSamples = 0, long targetSamples = 0,
        string detail = "Detector has an analyzable result.") =>
        new(detector, true, DetectorAvailabilityReason.AVAILABLE, detail,
            updatedAtUtc.ToUniversalTime(), eligibleSamples, targetSamples);

    public static DetectorAvailability Unavailable(string detector,
        DetectorAvailabilityReason reason, DateTimeOffset updatedAtUtc,
        long eligibleSamples = 0, long targetSamples = 0, string? detail = null) =>
        new(detector, false, reason, detail ?? reason.WireName(),
            updatedAtUtc.ToUniversalTime(), eligibleSamples, targetSamples);

    public static DetectorAvailability FromStatus(string detector, string? status,
        DateTimeOffset updatedAtUtc, long eligibleSamples = 0, long targetSamples = 0,
        string? detail = null)
    {
        var reason = DetectorAvailabilityReasonExtensions.FromDetectorStatus(status);
        return reason == DetectorAvailabilityReason.AVAILABLE
            ? Ready(detector, updatedAtUtc, eligibleSamples, targetSamples,
                detail ?? "Detector has an analyzable result.")
            : Unavailable(detector, reason, updatedAtUtc, eligibleSamples,
                targetSamples, detail);
    }
}

/// <summary>Inputs to the deterministic health classifier.  The booleans are
/// explicit so a caller cannot accidentally infer capability or freshness
/// merely because a numeric value exists.</summary>
public sealed record AcquisitionHealthInput(
    bool MonitorRunning,
    bool SourceConfigured,
    bool SourceAvailable,
    bool TimestampValid,
    bool Fresh,
    bool PowerAvailable,
    bool NativeFailure,
    bool SensorCharacterized,
    bool AnalysisAvailable,
    DateTimeOffset HostTimestampUtc,
    DateTimeOffset? SourceTimestampUtc = null,
    double? SampleAgeSeconds = null,
    string Source = "",
    string Detail = "",
    bool FreshnessVerified = true)
{
    public bool FreshnessKnown => FreshnessVerified && TimestampValid;
    public bool CapabilityKnown => SensorCharacterized;
}

/// <summary>Externally consumable health state.  MonitorRunning and
/// AnalysisAvailable are deliberately independent fields.</summary>
public sealed record AcquisitionHealthSnapshot(
    [property: JsonPropertyName("status")] AcquisitionHealthStatus Status,
    [property: JsonPropertyName("monitor_running")] bool MonitorRunning,
    [property: JsonPropertyName("analysis_available")] bool AnalysisAvailable,
    [property: JsonPropertyName("source_configured")] bool SourceConfigured,
    [property: JsonPropertyName("source_available")] bool SourceAvailable,
    [property: JsonPropertyName("timestamp_valid")] bool TimestampValid,
    [property: JsonPropertyName("fresh")] bool Fresh,
    [property: JsonPropertyName("power_available")] bool PowerAvailable,
    [property: JsonPropertyName("sensor_characterized")] bool SensorCharacterized,
    [property: JsonPropertyName("source")] string Source,
    [property: JsonPropertyName("host_timestamp_utc")] DateTimeOffset HostTimestampUtc,
    [property: JsonPropertyName("source_timestamp_utc")] DateTimeOffset? SourceTimestampUtc,
    [property: JsonPropertyName("sample_age_seconds")] double? SampleAgeSeconds,
    [property: JsonPropertyName("freshness_known")] bool FreshnessKnown,
    [property: JsonPropertyName("detectors")] IReadOnlyDictionary<string, DetectorAvailability> Detectors,
    [property: JsonPropertyName("detail")] string Detail)
{
    public bool MonitorAvailable => MonitorRunning && SourceAvailable &&
        Status is not AcquisitionHealthStatus.NATIVE_FAILURE;
    public bool CapabilityKnown => SensorCharacterized;
    public string StatusName => Status.WireName();
}

public static class AcquisitionHealth
{
    public static AcquisitionHealthStatus Classify(AcquisitionHealthInput input)
    {
        if (input.NativeFailure) return AcquisitionHealthStatus.NATIVE_FAILURE;
        if (!input.MonitorRunning || !input.SourceConfigured || !input.SourceAvailable)
            return AcquisitionHealthStatus.SOURCE_UNAVAILABLE;
        if (!input.TimestampValid) return AcquisitionHealthStatus.TIMESTAMP_INVALID;
        if (!input.Fresh) return AcquisitionHealthStatus.STALE;
        if (!input.FreshnessKnown) return AcquisitionHealthStatus.SENSOR_UNCHARACTERIZED;
        if (!input.PowerAvailable) return AcquisitionHealthStatus.POWER_UNAVAILABLE;
        if (!input.SensorCharacterized) return AcquisitionHealthStatus.SENSOR_UNCHARACTERIZED;
        return AcquisitionHealthStatus.HEALTHY;
    }

    public static AcquisitionHealthSnapshot Evaluate(AcquisitionHealthInput input,
        IReadOnlyDictionary<string, DetectorAvailability>? detectors = null)
    {
        if (input is null) throw new ArgumentNullException(nameof(input));
        var effectiveDetectors = detectors is null
            ? new Dictionary<string, DetectorAvailability>(StringComparer.Ordinal)
            : new Dictionary<string, DetectorAvailability>(detectors, StringComparer.Ordinal);
        return new(
            Classify(input),
            input.MonitorRunning,
            input.AnalysisAvailable,
            input.SourceConfigured,
            input.SourceAvailable,
            input.TimestampValid,
            input.Fresh,
            input.PowerAvailable,
            input.SensorCharacterized,
            input.Source,
            input.HostTimestampUtc.ToUniversalTime(),
            input.SourceTimestampUtc?.ToUniversalTime(),
            FiniteOrNull(input.SampleAgeSeconds),
            input.FreshnessKnown,
            effectiveDetectors,
            input.Detail);
    }

    public static AcquisitionHealthSnapshot FromElectricalSample(
        ElectricalSample? sample,
        DateTimeOffset hostTimestampUtc,
        bool monitorRunning,
        bool analysisAvailable,
        bool nativeFailure = false,
        bool sensorCharacterized = false,
        bool powerRequired = true,
        string source = "",
        string detail = "")
    {
        if (sample is null)
        {
            return Evaluate(new AcquisitionHealthInput(
                monitorRunning,
                SourceConfigured: !string.IsNullOrWhiteSpace(source),
                SourceAvailable: false,
                TimestampValid: false,
                Fresh: false,
                PowerAvailable: !powerRequired,
                NativeFailure: nativeFailure,
                SensorCharacterized: sensorCharacterized,
                AnalysisAvailable: analysisAvailable,
                HostTimestampUtc: hostTimestampUtc,
                Detail: detail,
                FreshnessVerified: false), null);
        }

        var sourceName = string.IsNullOrWhiteSpace(source) ? sample.Source : source;
        var timestampValid = sample.Freshness.Kind != FreshnessKind.Unavailable &&
            (!sample.Freshness.AgeSeconds.HasValue ||
             (double.IsFinite(sample.Freshness.AgeSeconds.Value) && sample.Freshness.AgeSeconds.Value >= 0));
        var powerAvailable = sample.Connector.HasPower || sample.ExternalPower.IsAvailable;
        return Evaluate(new AcquisitionHealthInput(
            monitorRunning,
            SourceConfigured: true,
            SourceAvailable: sample.Connector.HasVoltage,
            TimestampValid: timestampValid,
            Fresh: timestampValid && sample.Freshness.IsFresh,
            PowerAvailable: !powerRequired || powerAvailable,
            NativeFailure: nativeFailure,
            SensorCharacterized: sensorCharacterized,
            AnalysisAvailable: analysisAvailable,
            HostTimestampUtc: hostTimestampUtc,
            SourceTimestampUtc: sample.Freshness.SourceTimestampUtc,
            SampleAgeSeconds: sample.Freshness.AgeSeconds,
            Source: sourceName,
            Detail: detail,
            FreshnessVerified: sample.Freshness.TimestampVerified), null);
    }

    static double? FiniteOrNull(double? value) =>
        value is double number && double.IsFinite(number) && number >= 0 ? number : null;
}
