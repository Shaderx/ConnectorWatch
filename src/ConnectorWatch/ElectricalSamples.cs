using System.Text.Json.Serialization;

namespace ConnectorWatch;

/// <summary>Typed source seam used by the daemon.  IVoltageSource and its
/// Voltage return shape remain intact for existing consumers.</summary>
public interface IElectricalSource : IVoltageSource
{
    ElectricalSample ReadElectrical(DateTimeOffset now);
}

public static class ElectricalSourceExtensions
{
    /// <summary>
    /// Reads a typed sample from either a native typed provider or a legacy
    /// provider. Legacy power is classified as external sensor power and its
    /// source timestamp is evaluated against the supplied freshness bound.
    /// There is deliberately no provider fallback in this adapter.
    /// </summary>
    public static ElectricalSample ReadElectrical(this IVoltageSource source,
        DateTimeOffset now, double maxAgeSeconds, bool powerIsExternalSensor = true)
    {
        if (source is null) throw new ArgumentNullException(nameof(source));
        if (!double.IsFinite(maxAgeSeconds) || maxAgeSeconds < 0)
            throw new ArgumentOutOfRangeException(nameof(maxAgeSeconds));
        if (source is IElectricalSource typed) return typed.ReadElectrical(now);
        var legacy = source.Read(now);
        var freshness = FreshnessMetadata.SourceTimestamp(legacy.Timestamp, now, maxAgeSeconds,
            "legacy source timestamp accepted");
        return ElectricalSample.FromLegacy(legacy, source.Description, freshness,
            powerIsExternalSensor: powerIsExternalSensor);
    }
}

/// <summary>
/// The metric used to qualify comparable load.  These names are deliberately
/// part of the persisted/configuration contract; do not use enum ordinals on
/// the wire.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AnalysisLoadSource
{
    CONNECTOR_CURRENT,
    CONNECTOR_POWER,
    NVML_BOARD_POWER,
    EXTERNAL_SENSOR_POWER,

    // Friendly aliases for callers that prefer normal C# naming.  The
    // uppercase entries remain the canonical values and therefore ToString()
    // and WireName() use the issue/configuration spelling above.
    ConnectorCurrent = CONNECTOR_CURRENT,
    ConnectorPower = CONNECTOR_POWER,
    NvmlBoardPower = NVML_BOARD_POWER,
    ExternalSensorPower = EXTERNAL_SENSOR_POWER,
}

public static class AnalysisLoadSourceExtensions
{
    public static string WireName(this AnalysisLoadSource source) => source switch
    {
        AnalysisLoadSource.CONNECTOR_CURRENT => "CONNECTOR_CURRENT",
        AnalysisLoadSource.CONNECTOR_POWER => "CONNECTOR_POWER",
        AnalysisLoadSource.NVML_BOARD_POWER => "NVML_BOARD_POWER",
        AnalysisLoadSource.EXTERNAL_SENSOR_POWER => "EXTERNAL_SENSOR_POWER",
        _ => throw new ArgumentOutOfRangeException(nameof(source), source, "Unknown analysis load source."),
    };

    public static bool TryParse(string? value, out AnalysisLoadSource source)
    {
        source = default;
        if (string.IsNullOrWhiteSpace(value)) return false;
        var normalized = value.Trim().Replace('-', '_').Replace(' ', '_');
        return normalized.ToUpperInvariant() switch
        {
            "CONNECTOR_CURRENT" => Set(AnalysisLoadSource.CONNECTOR_CURRENT, out source),
            "CONNECTOR_POWER" => Set(AnalysisLoadSource.CONNECTOR_POWER, out source),
            "NVML_BOARD_POWER" => Set(AnalysisLoadSource.NVML_BOARD_POWER, out source),
            "EXTERNAL_SENSOR_POWER" => Set(AnalysisLoadSource.EXTERNAL_SENSOR_POWER, out source),
            _ => false,
        };
    }

    public static AnalysisLoadSource Parse(string value) =>
        TryParse(value, out var source)
            ? source
            : throw new ArgumentException(
                "AnalysisLoadSource must be CONNECTOR_CURRENT, CONNECTOR_POWER, " +
                "NVML_BOARD_POWER, or EXTERNAL_SENSOR_POWER.", nameof(value));

    static bool Set(AnalysisLoadSource value, out AnalysisLoadSource destination)
    {
        destination = value;
        return true;
    }
}

/// <summary>Why a field is absent.  Null alone cannot distinguish a sensor
/// that does not expose a field from one that failed to produce a sample.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ElectricalFieldAvailability
{
    Unsupported,
    Missing,
    Present,
}

/// <summary>Provenance for a power value.  A derived V×I value is one
/// observation, not a second independent sensor observation.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ElectricalPowerProvenance
{
    Unavailable,
    Measured,
    DerivedFromVoltageAndCurrent,
    Unknown,
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum FreshnessKind
{
    VerifiedSourceTimestamp,
    HostPollTimestampUnverified,
    Unavailable,
}

/// <summary>
/// Freshness is kept separate from the electrical values.  The native private
/// payload currently has no validated timestamp/sequence field, so its host
/// poll timestamp is intentionally marked unverified rather than presented as
/// hardware freshness.
/// </summary>
public sealed record FreshnessMetadata(
    DateTimeOffset HostTimestampUtc,
    DateTimeOffset? SourceTimestampUtc,
    double? AgeSeconds,
    bool IsFresh,
    FreshnessKind Kind,
    string Detail)
{
    public DateTimeOffset TimestampUtc => SourceTimestampUtc ?? HostTimestampUtc;
    public bool TimestampVerified => Kind == FreshnessKind.VerifiedSourceTimestamp;

    public static FreshnessMetadata HostPoll(DateTimeOffset hostTimestampUtc,
        string detail = "host-poll-time; native freshness unverified") =>
        new(hostTimestampUtc.ToUniversalTime(), null, 0, true,
            FreshnessKind.HostPollTimestampUnverified, detail);

    public static FreshnessMetadata SourceTimestamp(DateTimeOffset sourceTimestampUtc,
        DateTimeOffset hostTimestampUtc, double maxAgeSeconds,
        string detail = "source timestamp accepted")
    {
        var host = hostTimestampUtc.ToUniversalTime();
        var source = sourceTimestampUtc.ToUniversalTime();
        var age = (host - source).TotalSeconds;
        var fresh = double.IsFinite(age) && age >= 0 &&
            double.IsFinite(maxAgeSeconds) && maxAgeSeconds >= 0 && age <= maxAgeSeconds;
        return new(host, source, age, fresh, FreshnessKind.VerifiedSourceTimestamp, detail);
    }

    public static FreshnessMetadata Unavailable(DateTimeOffset hostTimestampUtc,
        string detail = "freshness unavailable") =>
        new(hostTimestampUtc.ToUniversalTime(), null, null, false,
            FreshnessKind.Unavailable, detail);
}

/// <summary>A scalar electrical field with explicit support/availability and
/// units.  Values marked Present are always finite.</summary>
public sealed record ElectricalMeasurement(
    double? Value,
    ElectricalFieldAvailability Availability,
    string Unit,
    string? Detail = null)
{
    public bool IsAvailable => Availability == ElectricalFieldAvailability.Present &&
        Value is double value && double.IsFinite(value);

    public static ElectricalMeasurement Present(double value, string unit, string? detail = null)
    {
        if (!double.IsFinite(value))
            throw new ArgumentOutOfRangeException(nameof(value), "Electrical values must be finite.");
        if (string.IsNullOrWhiteSpace(unit))
            throw new ArgumentException("An electrical unit is required.", nameof(unit));
        return new(value, ElectricalFieldAvailability.Present, unit, detail);
    }

    public static ElectricalMeasurement Missing(string unit, string? detail = null) =>
        new(null, ElectricalFieldAvailability.Missing, unit, detail);

    public static ElectricalMeasurement Unsupported(string unit, string? detail = null) =>
        new(null, ElectricalFieldAvailability.Unsupported, unit, detail);

    public double RequireValue() => IsAvailable
        ? Value!.Value
        : throw new InvalidOperationException(
            $"The {Unit} electrical field is {Availability}: {Detail ?? "no value"}.");
}

/// <summary>Typed voltage/current/power values for one physical rail.</summary>
public sealed record ElectricalRailSample(
    ElectricalMeasurement Voltage,
    ElectricalMeasurement Current,
    ElectricalMeasurement Power,
    ElectricalPowerProvenance PowerProvenance,
    string Source,
    DateTimeOffset TimestampUtc,
    FreshnessMetadata Freshness)
{
    public double? VoltageV => Voltage.IsAvailable ? Voltage.Value : null;
    public double? CurrentA => Current.IsAvailable ? Current.Value : null;
    public double? PowerW => Power.IsAvailable ? Power.Value : null;

    // Compatibility aliases make the typed contract easy to consume while
    // leaving the old Voltage record untouched.
    public double? Volts => VoltageV;
    public double? Amps => CurrentA;
    public double? Watts => PowerW;

    public bool HasVoltage => Voltage.IsAvailable;
    public bool HasCurrent => Current.IsAvailable;
    public bool HasPower => Power.IsAvailable;

    public static ElectricalRailSample Create(
        double? voltageV,
        double? currentA,
        double? powerW,
        ElectricalPowerProvenance powerProvenance,
        string source,
        DateTimeOffset timestampUtc,
        FreshnessMetadata freshness,
        ElectricalFieldAvailability voltageAvailability = ElectricalFieldAvailability.Present,
        ElectricalFieldAvailability currentAvailability = ElectricalFieldAvailability.Present,
        ElectricalFieldAvailability powerAvailability = ElectricalFieldAvailability.Present,
        string? detail = null)
    {
        var voltage = Field(voltageV, voltageAvailability, "V", detail);
        var current = Field(currentA, currentAvailability, "A", detail);
        var power = Field(powerW, powerAvailability, "W", detail);
        return new(voltage, current, power, powerProvenance, source, timestampUtc, freshness);
    }

    public static ElectricalRailSample Unsupported(string source, DateTimeOffset timestampUtc,
        FreshnessMetadata freshness, string? detail = null) =>
        new(ElectricalMeasurement.Unsupported("V", detail),
            ElectricalMeasurement.Unsupported("A", detail),
            ElectricalMeasurement.Unsupported("W", detail),
            ElectricalPowerProvenance.Unavailable, source, timestampUtc, freshness);

    static ElectricalMeasurement Field(double? value, ElectricalFieldAvailability availability,
        string unit, string? detail)
    {
        if (value is double number)
        {
            if (!double.IsFinite(number))
                throw new ArgumentOutOfRangeException(nameof(value), $"Electrical {unit} value must be finite.");
            if (availability == ElectricalFieldAvailability.Unsupported)
                throw new ArgumentException($"An unsupported {unit} field cannot carry a value.", nameof(availability));
            availability = ElectricalFieldAvailability.Present;
        }
        return availability switch
        {
            ElectricalFieldAvailability.Present when value.HasValue => ElectricalMeasurement.Present(value.Value, unit, detail),
            ElectricalFieldAvailability.Present => ElectricalMeasurement.Missing(unit, detail),
            ElectricalFieldAvailability.Missing => ElectricalMeasurement.Missing(unit, detail),
            ElectricalFieldAvailability.Unsupported => ElectricalMeasurement.Unsupported(unit, detail),
            _ => throw new ArgumentOutOfRangeException(nameof(availability)),
        };
    }
}

/// <summary>Optional power from an external sensor.  It is separate from the
/// direct connector rail's V×I result so the analysis basis cannot silently
/// substitute one for the other.</summary>
public sealed record ElectricalPowerReading(
    ElectricalMeasurement Power,
    ElectricalPowerProvenance Provenance,
    string Source,
    DateTimeOffset TimestampUtc,
    FreshnessMetadata Freshness)
{
    public double? PowerW => Power.IsAvailable ? Power.Value : null;
    public bool IsAvailable => Power.IsAvailable && Freshness.IsFresh;

    public static ElectricalPowerReading Unsupported(string source, DateTimeOffset timestampUtc,
        FreshnessMetadata freshness, string? detail = null) =>
        new(ElectricalMeasurement.Unsupported("W", detail),
            ElectricalPowerProvenance.Unavailable, source, timestampUtc, freshness);

    public static ElectricalPowerReading Measured(double powerW, string source,
        DateTimeOffset timestampUtc, FreshnessMetadata freshness, string? detail = null) =>
        new(ElectricalMeasurement.Present(powerW, "W", detail),
            ElectricalPowerProvenance.Measured, source, timestampUtc, freshness);
}

/// <summary>
/// A complete source observation.  RawExtras deliberately remains available
/// for legacy consumers, while analysis consumes the typed fields below.
/// </summary>
public sealed record ElectricalSample(
    DateTimeOffset TimestampUtc,
    string Source,
    ElectricalRailSample Connector,
    ElectricalRailSample Pcie,
    ElectricalPowerReading ExternalPower,
    FreshnessMetadata Freshness,
    string RawExtras)
{
    public DateTimeOffset Timestamp => TimestampUtc;
    public ElectricalRailSample TwelveVHpwr => Connector;
    public ElectricalRailSample Pcie12V => Pcie;
    public string Extras => RawExtras;
    public bool IsFresh => Freshness.IsFresh;

    /// <summary>Builds a typed sample from the legacy record without decoding
    /// its Extras.  Callers that know more fields should supply them directly.
    /// </summary>
    public static ElectricalSample FromLegacy(Voltage legacy, string source,
        FreshnessMetadata? freshness = null,
        ElectricalRailSample? pcie = null,
        bool powerIsExternalSensor = false,
        ElectricalPowerProvenance powerProvenance = ElectricalPowerProvenance.Measured)
    {
        if (legacy is null) throw new ArgumentNullException(nameof(legacy));
        var effectiveFreshness = freshness ?? FreshnessMetadata.HostPoll(legacy.Timestamp);
        var connector = ElectricalRailSample.Create(
            legacy.Volts,
            null,
            powerIsExternalSensor ? null : legacy.Power,
            powerIsExternalSensor
                ? ElectricalPowerProvenance.Unavailable
                : legacy.Power.HasValue ? powerProvenance : ElectricalPowerProvenance.Unavailable,
            source,
            legacy.Timestamp,
            effectiveFreshness,
            currentAvailability: ElectricalFieldAvailability.Unsupported,
            powerAvailability: powerIsExternalSensor || !legacy.Power.HasValue
                ? ElectricalFieldAvailability.Unsupported
                : ElectricalFieldAvailability.Present);
        var external = powerIsExternalSensor && legacy.Power is double externalPower
            ? ElectricalPowerReading.Measured(externalPower, source, legacy.Timestamp, effectiveFreshness)
            : ElectricalPowerReading.Unsupported(source, legacy.Timestamp, effectiveFreshness,
                "The selected source did not provide external sensor power.");
        return new(legacy.Timestamp, source, connector,
            pcie ?? ElectricalRailSample.Unsupported(source, legacy.Timestamp, effectiveFreshness,
                "PCIe fields are unsupported by this source."),
            external, effectiveFreshness, legacy.Extras);
    }

    public Voltage ToLegacyVoltage()
    {
        if (!Connector.VoltageV.HasValue)
            throw new InvalidOperationException("A legacy Voltage requires a connector voltage.");
        var power = Connector.PowerW ?? ExternalPower.PowerW;
        return new(TimestampUtc, Connector.VoltageV.Value, power, RawExtras);
    }

    /// <summary>
    /// Selects exactly the configured load basis.  Missing or stale data is
    /// represented as unavailable; this method intentionally has no fallback.
    /// </summary>
    public AnalysisLoadSelection SelectAnalysisLoad(AnalysisLoadSource source,
        double? nvmlBoardPower = null, FreshnessMetadata? nvmlFreshness = null)
    {
        switch (source)
        {
            case AnalysisLoadSource.CONNECTOR_CURRENT:
                return AnalysisLoadSelection.FromMeasurement(source, Connector.Current,
                    Connector.TimestampUtc, Connector.Freshness, "A");
            case AnalysisLoadSource.CONNECTOR_POWER:
                return AnalysisLoadSelection.FromMeasurement(source, Connector.Power,
                    Connector.TimestampUtc, Connector.Freshness, "W");
            case AnalysisLoadSource.EXTERNAL_SENSOR_POWER:
                return AnalysisLoadSelection.FromMeasurement(source, ExternalPower.Power,
                    ExternalPower.TimestampUtc, ExternalPower.Freshness, "W");
            case AnalysisLoadSource.NVML_BOARD_POWER:
                if (nvmlBoardPower is not double boardPower || !double.IsFinite(boardPower))
                    return AnalysisLoadSelection.Unavailable(source, sample: this, "NVML board power is unavailable.");
                var freshness = nvmlFreshness ?? FreshnessMetadata.HostPoll(TimestampUtc,
                    "NVML board-power timestamp is approximate to the source sample.");
                return freshness.IsFresh
                    ? new(source, boardPower, "W", TimestampUtc, freshness, true, "NVML board power")
                    : AnalysisLoadSelection.Unavailable(source, this, "NVML board-power freshness is unavailable.");
            default:
                throw new ArgumentOutOfRangeException(nameof(source), source, "Unknown analysis load source.");
        }
    }
}

public sealed record AnalysisLoadSelection(
    AnalysisLoadSource Source,
    double? Value,
    string Unit,
    DateTimeOffset TimestampUtc,
    FreshnessMetadata Freshness,
    bool IsAvailable,
    string Detail)
{
    public string SourceName => Source.WireName();
    public double? Watts => Unit == "W" ? Value : null;
    public double? Amps => Unit == "A" ? Value : null;

    /// <summary>
    /// Adapts a selected basis to the existing watt-binned detector. A current
    /// basis uses voltage from the same typed connector observation; amperes
    /// are never passed to a watt axis. Missing voltage makes analysis
    /// unavailable instead of introducing a fallback.
    /// </summary>
    public double? ToAnalysisPowerWatts(ElectricalSample sample)
    {
        if (!IsAvailable || Value is not double value || !double.IsFinite(value)) return null;
        if (Unit == "W") return value;
        if (Source != AnalysisLoadSource.CONNECTOR_CURRENT || Unit != "A" ||
            sample.Connector.VoltageV is not double voltage || !double.IsFinite(voltage) ||
            sample.Connector.TimestampUtc != TimestampUtc || !sample.Connector.Freshness.IsFresh)
            return null;
        var derived = value * voltage;
        return double.IsFinite(derived) ? derived : null;
    }

    public static AnalysisLoadSelection FromMeasurement(AnalysisLoadSource source,
        ElectricalMeasurement measurement, DateTimeOffset timestampUtc,
        FreshnessMetadata freshness, string unit)
    {
        var available = measurement.IsAvailable && freshness.IsFresh &&
            string.Equals(measurement.Unit, unit, StringComparison.OrdinalIgnoreCase) &&
            double.IsFinite(measurement.Value!.Value);
        return new(source, available ? measurement.Value : null, unit,
            timestampUtc, freshness, available,
            available ? "Selected typed electrical field." :
                $"Selected {source.WireName()} is {measurement.Availability} or stale.");
    }

    public static AnalysisLoadSelection Unavailable(AnalysisLoadSource source,
        ElectricalSample sample, string detail) =>
        new(source, null, source == AnalysisLoadSource.CONNECTOR_CURRENT ? "A" : "W",
            sample.TimestampUtc, FreshnessMetadata.Unavailable(sample.TimestampUtc, detail), false, detail);
}
