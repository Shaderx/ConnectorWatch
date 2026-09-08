using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ConnectorWatch;

/// <summary>
/// The one load-like quantity used by a differential model.  A model has
/// exactly one of these labels; callers must not combine connector current,
/// connector power, and board power as interchangeable load columns.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum DifferentialLoadProxy
{
    CONNECTOR_CURRENT,
    CONNECTOR_POWER,
    NVML_BOARD_POWER,
    EXTERNAL_SENSOR_POWER,

    ConnectorCurrent = CONNECTOR_CURRENT,
    ConnectorPower = CONNECTOR_POWER,
    NvmlBoardPower = NVML_BOARD_POWER,
    ExternalSensorPower = EXTERNAL_SENSOR_POWER,
}

public static class DifferentialLoadProxyExtensions
{
    public static string WireName(this DifferentialLoadProxy proxy) => proxy switch
    {
        DifferentialLoadProxy.CONNECTOR_CURRENT => "CONNECTOR_CURRENT",
        DifferentialLoadProxy.CONNECTOR_POWER => "CONNECTOR_POWER",
        DifferentialLoadProxy.NVML_BOARD_POWER => "NVML_BOARD_POWER",
        DifferentialLoadProxy.EXTERNAL_SENSOR_POWER => "EXTERNAL_SENSOR_POWER",
        _ => throw new ArgumentOutOfRangeException(nameof(proxy), proxy,
            "Unknown differential load proxy."),
    };

    public static string Unit(this DifferentialLoadProxy proxy) => proxy switch
    {
        DifferentialLoadProxy.CONNECTOR_CURRENT => "A",
        DifferentialLoadProxy.CONNECTOR_POWER or
            DifferentialLoadProxy.NVML_BOARD_POWER or
            DifferentialLoadProxy.EXTERNAL_SENSOR_POWER => "W",
        _ => throw new ArgumentOutOfRangeException(nameof(proxy), proxy,
            "Unknown differential load proxy."),
    };

    public static AnalysisLoadSource ToAnalysisLoadSource(this DifferentialLoadProxy proxy) =>
        proxy switch
        {
            DifferentialLoadProxy.CONNECTOR_CURRENT => AnalysisLoadSource.CONNECTOR_CURRENT,
            DifferentialLoadProxy.CONNECTOR_POWER => AnalysisLoadSource.CONNECTOR_POWER,
            DifferentialLoadProxy.NVML_BOARD_POWER => AnalysisLoadSource.NVML_BOARD_POWER,
            DifferentialLoadProxy.EXTERNAL_SENSOR_POWER => AnalysisLoadSource.EXTERNAL_SENSOR_POWER,
            _ => throw new ArgumentOutOfRangeException(nameof(proxy), proxy,
                "Unknown differential load proxy."),
        };

    public static DifferentialLoadProxy FromAnalysisLoadSource(AnalysisLoadSource source) =>
        source switch
        {
            AnalysisLoadSource.CONNECTOR_CURRENT => DifferentialLoadProxy.CONNECTOR_CURRENT,
            AnalysisLoadSource.CONNECTOR_POWER => DifferentialLoadProxy.CONNECTOR_POWER,
            AnalysisLoadSource.NVML_BOARD_POWER => DifferentialLoadProxy.NVML_BOARD_POWER,
            AnalysisLoadSource.EXTERNAL_SENSOR_POWER => DifferentialLoadProxy.EXTERNAL_SENSOR_POWER,
            _ => throw new ArgumentOutOfRangeException(nameof(source), source,
                "Unknown analysis load source."),
        };
}

/// <summary>Named columns supported by the differential model.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum DifferentialFeatureKind
{
    LOAD_PROXY,
    BOARD_POWER,
    TEMPERATURE,
    FAN_PERCENT,
    THERMAL_STATE,

    LoadProxy = LOAD_PROXY,
    BoardPower = BOARD_POWER,
    Temperature = TEMPERATURE,
    FanPercent = FAN_PERCENT,
    ThermalState = THERMAL_STATE,
}

/// <summary>Why one observation was not admitted to a fit or prediction.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum DifferentialSampleRejectionReason
{
    NONE,
    MISSING_VOLTAGE,
    MISSING_FEATURE,
    NOT_FRESH,
    AGE_EXCEEDED,
    NOT_SYNCHRONIZED,
    INVALID_TIMESTAMP,
    NON_FINITE,
    OUT_OF_RANGE,
    NOT_SETTLED,
    OUTSIDE_ENVELOPE,
    IDENTITY_MISMATCH,

    MissingVoltage = MISSING_VOLTAGE,
    MissingFeature = MISSING_FEATURE,
    NotFresh = NOT_FRESH,
    AgeExceeded = AGE_EXCEEDED,
    NotSynchronized = NOT_SYNCHRONIZED,
    InvalidTimestamp = INVALID_TIMESTAMP,
    NonFinite = NON_FINITE,
    OutOfRange = OUT_OF_RANGE,
    NotSettled = NOT_SETTLED,
    OutsideEnvelope = OUTSIDE_ENVELOPE,
    IdentityMismatch = IDENTITY_MISMATCH,
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum DifferentialModelState
{
    FITTED,
    INSUFFICIENT_DATA,
    ILL_CONDITIONED,
    INVALID,

    Fitted = FITTED,
    InsufficientData = INSUFFICIENT_DATA,
    IllConditioned = ILL_CONDITIONED,
    Invalid = INVALID,
}

/// <summary>
/// Scalar feature values for one synchronized observation.  Nullable fields
/// are intentional: unsupported telemetry is absent and never reconstructed
/// from a different sensor.  ThermalStateValue is a documented numeric state
/// code supplied by the adapter (for example, 0=normal and 1=throttled).
/// </summary>
public record DifferentialFeatureVector
{
    [JsonPropertyName("connector_current_a")]
    public double? ConnectorCurrentA { get; init; }

    [JsonPropertyName("connector_power_w")]
    public double? ConnectorPowerW { get; init; }

    [JsonPropertyName("board_power_w")]
    public double? BoardPowerW { get; init; }

    [JsonPropertyName("external_sensor_power_w")]
    public double? ExternalSensorPowerW { get; init; }

    [JsonPropertyName("temperature_c")]
    public double? TemperatureC { get; init; }

    [JsonPropertyName("fan_percent")]
    public double? FanPercent { get; init; }

    [JsonPropertyName("thermal_state")]
    public double? ThermalStateValue { get; init; }

    [JsonConstructor]
    public DifferentialFeatureVector(
        double? connectorCurrentA = null,
        double? connectorPowerW = null,
        double? boardPowerW = null,
        double? externalSensorPowerW = null,
        double? temperatureC = null,
        double? fanPercent = null,
        double? thermalStateValue = null)
    {
        ConnectorCurrentA = connectorCurrentA;
        ConnectorPowerW = connectorPowerW;
        BoardPowerW = boardPowerW;
        ExternalSensorPowerW = externalSensorPowerW;
        TemperatureC = temperatureC;
        FanPercent = fanPercent;
        ThermalStateValue = thermalStateValue;
    }

    public double? ConnectorCurrent => ConnectorCurrentA;
    public double? ConnectorPower => ConnectorPowerW;
    public double? NvmlBoardPowerW => BoardPowerW;
    public double? Temperature => TemperatureC;
    public double? Fan => FanPercent;
    public double? FanThermalState => ThermalStateValue;
    public double? ThermalState => ThermalStateValue;

    public double? GetLoad(DifferentialLoadProxy proxy) => proxy switch
    {
        DifferentialLoadProxy.CONNECTOR_CURRENT => ConnectorCurrentA,
        DifferentialLoadProxy.CONNECTOR_POWER => ConnectorPowerW,
        DifferentialLoadProxy.NVML_BOARD_POWER => BoardPowerW,
        DifferentialLoadProxy.EXTERNAL_SENSOR_POWER => ExternalSensorPowerW,
        _ => throw new ArgumentOutOfRangeException(nameof(proxy), proxy,
            "Unknown differential load proxy."),
    };

    public bool TryGet(DifferentialFeatureKind feature, DifferentialLoadProxy proxy,
        out double value)
    {
        double? candidate = feature switch
        {
            DifferentialFeatureKind.LOAD_PROXY => GetLoad(proxy),
            DifferentialFeatureKind.BOARD_POWER => BoardPowerW,
            DifferentialFeatureKind.TEMPERATURE => TemperatureC,
            DifferentialFeatureKind.FAN_PERCENT => FanPercent,
            DifferentialFeatureKind.THERMAL_STATE => ThermalStateValue,
            _ => throw new ArgumentOutOfRangeException(nameof(feature), feature,
                "Unknown differential feature."),
        };
        if (candidate is double number && double.IsFinite(number))
        {
            value = number;
            return true;
        }
        value = default;
        return false;
    }

    public static DifferentialFeatureVector Create(
        double? connectorCurrentA = null,
        double? connectorPowerW = null,
        double? boardPowerW = null,
        double? temperatureC = null,
        double? fanPercent = null,
        double? thermalStateValue = null,
        double? externalSensorPowerW = null) =>
        new(connectorCurrentA, connectorPowerW, boardPowerW, externalSensorPowerW,
            temperatureC, fanPercent, thermalStateValue);
}

/// <summary>
/// Compatibility spelling for adapters that call the vector "features".
/// It carries exactly the same fields and has no alternate load semantics.
/// </summary>
public sealed record DifferentialFeatures : DifferentialFeatureVector
{
    [JsonConstructor]
    public DifferentialFeatures(
        double? connectorCurrentA = null,
        double? connectorPowerW = null,
        double? boardPowerW = null,
        double? externalSensorPowerW = null,
        double? temperatureC = null,
        double? fanPercent = null,
        double? thermalStateValue = null)
        : base(connectorCurrentA, connectorPowerW, boardPowerW, externalSensorPowerW,
            temperatureC, fanPercent, thermalStateValue) { }
}

/// <summary>
/// One voltage/features observation.  Quality flags are carried by the
/// observation so replaying a capture uses exactly the same qualification
/// decisions as the live path.
/// </summary>
public sealed record DifferentialSample
{
    [JsonPropertyName("timestamp_utc")]
    public DateTimeOffset TimestampUtc { get; init; }

    [JsonPropertyName("input_voltage_v")]
    public double? InputVoltageV { get; init; }

    [JsonPropertyName("features")]
    public DifferentialFeatureVector Features { get; init; }

    [JsonPropertyName("is_fresh")]
    public bool IsFresh { get; init; }

    [JsonPropertyName("is_synchronized")]
    public bool IsSynchronized { get; init; }

    [JsonPropertyName("is_settled")]
    public bool IsSettled { get; init; }

    [JsonPropertyName("age_seconds")]
    public double? AgeSeconds { get; init; }

    [JsonPropertyName("voltage_timestamp_utc")]
    public DateTimeOffset? VoltageTimestampUtc { get; init; }

    [JsonPropertyName("feature_timestamp_utc")]
    public DateTimeOffset? FeatureTimestampUtc { get; init; }

    [JsonPropertyName("identity")]
    public string? Identity { get; init; }

    [JsonConstructor]
    public DifferentialSample(
        DateTimeOffset timestampUtc,
        double? inputVoltageV,
        DifferentialFeatureVector features,
        bool isFresh = true,
        bool isSynchronized = true,
        bool isSettled = true,
        double? ageSeconds = null,
        DateTimeOffset? voltageTimestampUtc = null,
        DateTimeOffset? featureTimestampUtc = null,
        string? identity = null)
    {
        TimestampUtc = timestampUtc;
        InputVoltageV = inputVoltageV;
        Features = features ?? throw new ArgumentNullException(nameof(features));
        IsFresh = isFresh;
        IsSynchronized = isSynchronized;
        IsSettled = isSettled;
        AgeSeconds = ageSeconds;
        VoltageTimestampUtc = voltageTimestampUtc;
        FeatureTimestampUtc = featureTimestampUtc;
        Identity = string.IsNullOrWhiteSpace(identity) ? null : identity.Trim();
    }

    public DifferentialSample(
        DateTimeOffset timestampUtc,
        double inputVoltageV,
        DifferentialFeatureVector features,
        bool isFresh = true,
        bool isSynchronized = true,
        bool isSettled = true)
        : this(timestampUtc, (double?)inputVoltageV, features, isFresh,
            isSynchronized, isSettled) { }

    public DateTimeOffset Timestamp => TimestampUtc;
    public double? VoltageV => InputVoltageV;
    public double? InputVoltage => InputVoltageV;
    public DifferentialFeatureVector FeatureVector => Features;

    public static DifferentialSample Create(
        DateTimeOffset timestampUtc,
        double? inputVoltageV,
        DifferentialFeatureVector features,
        bool isFresh = true,
        bool isSynchronized = true,
        bool isSettled = true,
        double? ageSeconds = null,
        DateTimeOffset? voltageTimestampUtc = null,
        DateTimeOffset? featureTimestampUtc = null,
        string? identity = null) =>
        new(timestampUtc, inputVoltageV, features, isFresh, isSynchronized,
            isSettled, ageSeconds, voltageTimestampUtc, featureTimestampUtc, identity);

    public static DifferentialSample FromElectricalSample(
        ElectricalSample sample,
        double? boardPowerW = null,
        double? temperatureC = null,
        double? fanPercent = null,
        double? thermalStateValue = null,
        bool isSettled = true,
        string? identity = null)
    {
        if (sample is null) throw new ArgumentNullException(nameof(sample));
        var features = DifferentialFeatureVector.Create(
            sample.Connector.CurrentA,
            sample.Connector.PowerW,
            boardPowerW,
            temperatureC,
            fanPercent,
            thermalStateValue,
            sample.ExternalPower.PowerW);
        return new(sample.TimestampUtc, sample.Connector.VoltageV, features,
            sample.Freshness.IsFresh, true, isSettled,
            sample.Freshness.AgeSeconds, sample.TimestampUtc,
            sample.TimestampUtc, identity);
    }
}

/// <summary>A closed interval with a stable unit label.</summary>
public sealed record DifferentialFeatureRange
{
    [JsonPropertyName("minimum")]
    public double Minimum { get; }

    [JsonPropertyName("maximum")]
    public double Maximum { get; }

    [JsonPropertyName("unit")]
    public string Unit { get; }

    [JsonConstructor]
    public DifferentialFeatureRange(double minimum, double maximum, string unit)
    {
        if (!double.IsFinite(minimum) || !double.IsFinite(maximum) || minimum > maximum)
            throw new ArgumentOutOfRangeException(nameof(minimum),
                "Feature bounds must be finite and ordered.");
        if (string.IsNullOrWhiteSpace(unit))
            throw new ArgumentException("Feature bounds require a unit.", nameof(unit));
        Minimum = minimum;
        Maximum = maximum;
        Unit = unit.Trim();
    }

    public double Span => Maximum - Minimum;
    public bool Contains(double value) => value >= Minimum && value <= Maximum;
    public bool Contains(double value, double margin) =>
        value >= Minimum - margin && value <= Maximum + margin;
}

/// <summary>
/// Configured physical ranges.  Null disables a configured range; the model
/// still requires selected values to be finite.  Defaults are conservative
/// telemetry domains, not safety limits.
/// </summary>
public sealed record DifferentialValueBounds
{
    [JsonPropertyName("load_proxy")]
    public DifferentialFeatureRange? LoadProxy { get; init; }

    [JsonPropertyName("board_power")]
    public DifferentialFeatureRange? BoardPower { get; init; }

    [JsonPropertyName("temperature")]
    public DifferentialFeatureRange? Temperature { get; init; }

    [JsonPropertyName("fan_percent")]
    public DifferentialFeatureRange? FanPercent { get; init; }

    [JsonPropertyName("thermal_state")]
    public DifferentialFeatureRange? ThermalState { get; init; }

    public static DifferentialValueBounds Default => new()
    {
        LoadProxy = new(0, 10_000, "native"),
        BoardPower = new(0, 10_000, "W"),
        Temperature = new(-100, 250, "C"),
        FanPercent = new(0, 100, "%"),
        ThermalState = new(-10_000, 10_000, "code"),
    };

    public DifferentialFeatureRange? For(DifferentialFeatureKind feature) => feature switch
    {
        DifferentialFeatureKind.LOAD_PROXY => LoadProxy,
        DifferentialFeatureKind.BOARD_POWER => BoardPower,
        DifferentialFeatureKind.TEMPERATURE => Temperature,
        DifferentialFeatureKind.FAN_PERCENT => FanPercent,
        DifferentialFeatureKind.THERMAL_STATE => ThermalState,
        _ => throw new ArgumentOutOfRangeException(nameof(feature), feature,
            "Unknown differential feature."),
    };
}

/// <summary>Identity that makes a persisted artifact replayable and scoped.</summary>
public sealed record DifferentialModelIdentity
{
    [JsonPropertyName("identity_version")]
    public int IdentityVersion { get; init; } = 1;

    [JsonPropertyName("gpu_uuid")]
    public string GpuUuid { get; init; } = "unspecified";

    [JsonPropertyName("board")]
    public string Board { get; init; } = "unspecified";

    [JsonPropertyName("driver")]
    public string Driver { get; init; } = "unspecified";

    [JsonPropertyName("voltage_source")]
    public string VoltageSource { get; init; } = "unspecified";

    [JsonPropertyName("configuration_id")]
    public string ConfigurationId { get; init; } = "unspecified";

    [JsonConstructor]
    public DifferentialModelIdentity(
        int identityVersion = 1,
        string gpuUuid = "unspecified",
        string board = "unspecified",
        string driver = "unspecified",
        string voltageSource = "unspecified",
        string configurationId = "unspecified")
    {
        if (identityVersion < 1) throw new ArgumentOutOfRangeException(nameof(identityVersion));
        IdentityVersion = identityVersion;
        GpuUuid = Required(gpuUuid, nameof(gpuUuid));
        Board = Required(board, nameof(board));
        Driver = Required(driver, nameof(driver));
        VoltageSource = Required(voltageSource, nameof(voltageSource));
        ConfigurationId = Required(configurationId, nameof(configurationId));
    }

    public static DifferentialModelIdentity Unspecified => new();

    public bool IsUnspecified =>
        string.Equals(GpuUuid, "unspecified", StringComparison.OrdinalIgnoreCase) &&
        string.Equals(Board, "unspecified", StringComparison.OrdinalIgnoreCase) &&
        string.Equals(Driver, "unspecified", StringComparison.OrdinalIgnoreCase) &&
        string.Equals(VoltageSource, "unspecified", StringComparison.OrdinalIgnoreCase) &&
        string.Equals(ConfigurationId, "unspecified", StringComparison.OrdinalIgnoreCase);

    public string CanonicalKey =>
        $"v{IdentityVersion}|gpu={GpuUuid}|board={Board}|driver={Driver}|source={VoltageSource}|config={ConfigurationId}";

    public bool Matches(DifferentialModelIdentity? other) => other is not null &&
        IdentityVersion == other.IdentityVersion &&
        string.Equals(GpuUuid, other.GpuUuid, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(Board, other.Board, StringComparison.Ordinal) &&
        string.Equals(Driver, other.Driver, StringComparison.Ordinal) &&
        string.Equals(VoltageSource, other.VoltageSource, StringComparison.Ordinal) &&
        string.Equals(ConfigurationId, other.ConfigurationId, StringComparison.Ordinal);

    static string Required(string? value, string name) => string.IsNullOrWhiteSpace(value)
        ? throw new ArgumentException("An identity value is required.", name)
        : value.Trim();
}

/// <summary>
/// All gates and algorithm choices are copied into an artifact.  This avoids
/// silently evaluating an old model with new qualification semantics.
/// </summary>
public sealed record DifferentialModelOptions
{
    public const int CurrentSchemaVersion = 1;
    public const string CurrentAlgorithmVersion = "HUBER_IRLS_V1";
    public const string CurrentFeatureVersion = "DIFFERENTIAL_FEATURES_V1";

    [JsonPropertyName("schema_version")]
    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    [JsonPropertyName("algorithm_version")]
    public string AlgorithmVersion { get; init; } = CurrentAlgorithmVersion;

    [JsonPropertyName("feature_version")]
    public string FeatureVersion { get; init; } = CurrentFeatureVersion;

    [JsonPropertyName("load_proxy")]
    public DifferentialLoadProxy LoadProxy { get; init; } = DifferentialLoadProxy.CONNECTOR_POWER;

    [JsonPropertyName("include_board_power")]
    public bool IncludeBoardPower { get; init; } = true;

    [JsonPropertyName("include_temperature")]
    public bool IncludeTemperature { get; init; } = true;

    [JsonPropertyName("include_fan_percent")]
    public bool IncludeFanPercent { get; init; }

    [JsonPropertyName("include_thermal_state")]
    public bool IncludeThermalState { get; init; }

    [JsonPropertyName("minimum_samples")]
    public int MinimumSamples { get; init; } = 8;

    [JsonPropertyName("minimum_slope_samples")]
    public int MinimumSlopeSamples { get; init; } = 8;

    [JsonPropertyName("minimum_load_span")]
    public double MinimumLoadSpan { get; init; } = 1;

    [JsonPropertyName("huber_delta_volts")]
    public double HuberDeltaVolts { get; init; } = .05;

    [JsonPropertyName("maximum_iterations")]
    public int MaximumIterations { get; init; } = 50;

    [JsonPropertyName("convergence_tolerance")]
    public double ConvergenceTolerance { get; init; } = 1e-8;

    [JsonPropertyName("maximum_condition_number")]
    public double MaximumConditionNumber { get; init; } = 1e8;

    [JsonPropertyName("max_sample_age_seconds")]
    public double MaxSampleAgeSeconds { get; init; } = 5;

    [JsonPropertyName("synchronization_tolerance_seconds")]
    public double SynchronizationToleranceSeconds { get; init; } = .5;

    [JsonPropertyName("envelope_margin_fraction")]
    public double EnvelopeMarginFraction { get; init; } = .05;

    [JsonPropertyName("excess_droop_allowance_volts")]
    public double ExcessDroopAllowanceVolts { get; init; }

    [JsonPropertyName("require_fresh")]
    public bool RequireFresh { get; init; } = true;

    [JsonPropertyName("require_synchronized")]
    public bool RequireSynchronized { get; init; } = true;

    [JsonPropertyName("require_settled")]
    public bool RequireSettled { get; init; } = true;

    [JsonPropertyName("reject_identity_mismatch")]
    public bool RejectIdentityMismatch { get; init; } = true;

    [JsonPropertyName("bounds")]
    public DifferentialValueBounds Bounds { get; init; } = DifferentialValueBounds.Default;

    [JsonPropertyName("identity")]
    public DifferentialModelIdentity Identity { get; init; } = DifferentialModelIdentity.Unspecified;

    [JsonPropertyName("artifact_created_at_utc")]
    public DateTimeOffset? ArtifactCreatedAtUtc { get; init; }

    // Friendly aliases used by integrations that describe the selected load
    // as a source rather than a proxy.  They still resolve to one enum value.
    [JsonIgnore]
    public AnalysisLoadSource AnalysisLoadSource
    {
        get => LoadProxy.ToAnalysisLoadSource();
        init => LoadProxy = DifferentialLoadProxyExtensions.FromAnalysisLoadSource(value);
    }

    [JsonIgnore]
    public int MinimumQualifiedSamples
    {
        get => MinimumSamples;
        init => MinimumSamples = value;
    }

    public void Validate()
    {
        if (SchemaVersion != CurrentSchemaVersion)
            throw new NotSupportedException($"Unsupported differential model schema {SchemaVersion}.");
        if (!string.Equals(AlgorithmVersion, CurrentAlgorithmVersion, StringComparison.Ordinal))
            throw new NotSupportedException($"Unsupported differential model algorithm '{AlgorithmVersion}'.");
        if (!string.Equals(FeatureVersion, CurrentFeatureVersion, StringComparison.Ordinal))
            throw new NotSupportedException($"Unsupported differential model feature version '{FeatureVersion}'.");
        _ = LoadProxy.WireName();
        if (IncludeBoardPower && LoadProxy == DifferentialLoadProxy.NVML_BOARD_POWER)
        {
            // The selected board power column is already the one load column;
            // this flag is harmless but does not create a duplicate feature.
        }
        if (MinimumSamples < 2 || MinimumSlopeSamples < 2 ||
            MinimumSlopeSamples > MinimumSamples ||
            !double.IsFinite(MinimumLoadSpan) || MinimumLoadSpan < 0 ||
            !double.IsFinite(HuberDeltaVolts) || HuberDeltaVolts <= 0 ||
            MaximumIterations < 1 ||
            !double.IsFinite(ConvergenceTolerance) || ConvergenceTolerance <= 0 ||
            !double.IsFinite(MaximumConditionNumber) || MaximumConditionNumber <= 1 ||
            !double.IsFinite(MaxSampleAgeSeconds) || MaxSampleAgeSeconds < 0 ||
            !double.IsFinite(SynchronizationToleranceSeconds) || SynchronizationToleranceSeconds < 0 ||
            !double.IsFinite(EnvelopeMarginFraction) || EnvelopeMarginFraction < 0 ||
            !double.IsFinite(ExcessDroopAllowanceVolts) || ExcessDroopAllowanceVolts < 0)
            throw new ArgumentOutOfRangeException(nameof(MinimumSamples),
                "Differential model settings must be finite and positive where required.");
        if (Bounds is null) throw new ArgumentNullException(nameof(Bounds));
        if (Identity is null) throw new ArgumentNullException(nameof(Identity));
        ValidateRange(Bounds.LoadProxy, "load proxy");
        ValidateRange(Bounds.BoardPower, "board power");
        ValidateRange(Bounds.Temperature, "temperature");
        ValidateRange(Bounds.FanPercent, "fan percent");
        ValidateRange(Bounds.ThermalState, "thermal state");
    }

    static void ValidateRange(DifferentialFeatureRange? range, string name)
    {
        if (range is not null && string.IsNullOrWhiteSpace(range.Unit))
            throw new ArgumentException($"The {name} range has no unit.");
    }
}

/// <summary>One frozen normalized regression coefficient and its envelope.</summary>
public sealed record DifferentialModelCoefficient
{
    [JsonPropertyName("feature")]
    public DifferentialFeatureKind Feature { get; }

    [JsonPropertyName("name")]
    public string Name { get; }

    [JsonPropertyName("unit")]
    public string Unit { get; }

    [JsonPropertyName("coefficient_v_per_unit")]
    public double CoefficientVoltsPerUnit { get; }

    [JsonPropertyName("center")]
    public double Center { get; }

    [JsonPropertyName("scale")]
    public double Scale { get; }

    [JsonPropertyName("envelope")]
    public DifferentialFeatureRange Envelope { get; }

    [JsonConstructor]
    public DifferentialModelCoefficient(
        DifferentialFeatureKind feature,
        string name,
        string unit,
        double coefficientVoltsPerUnit,
        double center,
        double scale,
        DifferentialFeatureRange envelope)
    {
        if (!Enum.IsDefined(feature)) throw new ArgumentOutOfRangeException(nameof(feature));
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("A feature name is required.", nameof(name));
        if (string.IsNullOrWhiteSpace(unit)) throw new ArgumentException("A feature unit is required.", nameof(unit));
        if (!double.IsFinite(coefficientVoltsPerUnit) || !double.IsFinite(center) ||
            !double.IsFinite(scale) || scale <= 0)
            throw new ArgumentOutOfRangeException(nameof(coefficientVoltsPerUnit),
                "Regression coefficients must be finite and scaled.");
        if (envelope is null) throw new ArgumentNullException(nameof(envelope));
        Feature = feature;
        Name = name.Trim();
        Unit = unit.Trim();
        CoefficientVoltsPerUnit = coefficientVoltsPerUnit;
        Center = center;
        Scale = scale;
        Envelope = envelope;
    }

    public double Coefficient => CoefficientVoltsPerUnit;
}

/// <summary>Immutable result of qualifying one observation.</summary>
public sealed record DifferentialSampleQualification
{
    [JsonPropertyName("is_qualified")]
    public bool IsQualified { get; }

    [JsonPropertyName("is_fresh")]
    public bool IsFresh { get; }

    [JsonPropertyName("is_synchronized")]
    public bool IsSynchronized { get; }

    [JsonPropertyName("is_finite")]
    public bool IsFinite { get; }

    [JsonPropertyName("is_in_range")]
    public bool IsInRange { get; }

    [JsonPropertyName("is_settled")]
    public bool IsSettled { get; }

    [JsonPropertyName("is_in_envelope")]
    public bool IsInEnvelope { get; }

    [JsonPropertyName("reasons")]
    public IReadOnlyList<DifferentialSampleRejectionReason> Reasons { get; }

    [JsonPropertyName("detail")]
    public string Detail { get; }

    [JsonConstructor]
    public DifferentialSampleQualification(
        bool isQualified,
        bool isFresh,
        bool isSynchronized,
        bool isFinite,
        bool isInRange,
        bool isSettled,
        bool isInEnvelope,
        IReadOnlyList<DifferentialSampleRejectionReason>? reasons,
        string? detail = null)
    {
        IsFresh = isFresh;
        IsSynchronized = isSynchronized;
        IsFinite = isFinite;
        IsInRange = isInRange;
        IsSettled = isSettled;
        IsInEnvelope = isInEnvelope;
        Reasons = new ReadOnlyCollection<DifferentialSampleRejectionReason>(
            (reasons ?? Array.Empty<DifferentialSampleRejectionReason>()).Distinct().ToList());
        IsQualified = isQualified && Reasons.Count == 0;
        Detail = detail ?? (IsQualified ? "Qualified differential sample." :
            string.Join(", ", Reasons.Select(x => x.ToString())));
    }

    public bool Accepted => IsQualified;
    public IReadOnlyList<DifferentialSampleRejectionReason> FailureReasons => Reasons;
}

/// <summary>Fit diagnostics are persisted with the coefficients.</summary>
public sealed record DifferentialModelDiagnostics
{
    [JsonPropertyName("state")]
    public DifferentialModelState State { get; }

    [JsonPropertyName("input_samples")]
    public int InputSamples { get; }

    [JsonPropertyName("qualified_samples")]
    public int QualifiedSamples { get; }

    [JsonPropertyName("rejected_samples")]
    public int RejectedSamples { get; }

    [JsonPropertyName("iterations")]
    public int Iterations { get; }

    [JsonPropertyName("effective_rank")]
    public int EffectiveRank { get; }

    [JsonPropertyName("condition_number")]
    public double? ConditionNumber { get; }

    [JsonPropertyName("load_span")]
    public double? LoadSpan { get; }

    [JsonPropertyName("rmse_v")]
    public double? RootMeanSquareErrorVolts { get; }

    [JsonPropertyName("median_absolute_error_v")]
    public double? MedianAbsoluteErrorVolts { get; }

    [JsonPropertyName("max_absolute_error_v")]
    public double? MaximumAbsoluteErrorVolts { get; }

    [JsonPropertyName("robust_scale_v")]
    public double? RobustScaleVolts { get; }

    [JsonPropertyName("slope_available")]
    public bool SlopeAvailable { get; }

    [JsonPropertyName("rejections")]
    public IReadOnlyDictionary<DifferentialSampleRejectionReason, int> Rejections { get; }

    [JsonPropertyName("detail")]
    public string Detail { get; }

    [JsonConstructor]
    public DifferentialModelDiagnostics(
        DifferentialModelState state,
        int inputSamples,
        int qualifiedSamples,
        int rejectedSamples,
        int iterations,
        int effectiveRank,
        double? conditionNumber,
        double? loadSpan,
        double? rootMeanSquareErrorVolts,
        double? medianAbsoluteErrorVolts,
        double? maximumAbsoluteErrorVolts,
        double? robustScaleVolts,
        bool slopeAvailable,
        IReadOnlyDictionary<DifferentialSampleRejectionReason, int>? rejections,
        string detail)
    {
        if (!Enum.IsDefined(state)) throw new ArgumentOutOfRangeException(nameof(state));
        if (inputSamples < 0 || qualifiedSamples < 0 || rejectedSamples < 0 ||
            iterations < 0 || effectiveRank < 0)
            throw new ArgumentOutOfRangeException(nameof(inputSamples));
        ValidateFinite(conditionNumber, nameof(conditionNumber));
        ValidateFinite(loadSpan, nameof(loadSpan));
        ValidateFinite(rootMeanSquareErrorVolts, nameof(rootMeanSquareErrorVolts));
        ValidateFinite(medianAbsoluteErrorVolts, nameof(medianAbsoluteErrorVolts));
        ValidateFinite(maximumAbsoluteErrorVolts, nameof(maximumAbsoluteErrorVolts));
        ValidateFinite(robustScaleVolts, nameof(robustScaleVolts));
        State = state;
        InputSamples = inputSamples;
        QualifiedSamples = qualifiedSamples;
        RejectedSamples = rejectedSamples;
        Iterations = iterations;
        EffectiveRank = effectiveRank;
        ConditionNumber = conditionNumber;
        LoadSpan = loadSpan;
        RootMeanSquareErrorVolts = rootMeanSquareErrorVolts;
        MedianAbsoluteErrorVolts = medianAbsoluteErrorVolts;
        MaximumAbsoluteErrorVolts = maximumAbsoluteErrorVolts;
        RobustScaleVolts = robustScaleVolts;
        SlopeAvailable = slopeAvailable;
        Rejections = new ReadOnlyDictionary<DifferentialSampleRejectionReason, int>(
            (rejections ?? new Dictionary<DifferentialSampleRejectionReason, int>())
                .ToDictionary(x => x.Key, x => x.Value));
        Detail = detail ?? string.Empty;
    }

    public int QualifiedCount => QualifiedSamples;
    public double? RmseVolts => RootMeanSquareErrorVolts;
    public double? MaxAbsoluteResidualVolts => MaximumAbsoluteErrorVolts;

    static void ValidateFinite(double? value, string name)
    {
        if (value is double number && (!double.IsFinite(number) || number < 0))
            throw new ArgumentOutOfRangeException(name);
    }
}

/// <summary>Expected voltage and residual for one qualified observation.</summary>
public sealed record DifferentialPrediction
{
    [JsonPropertyName("timestamp_utc")]
    public DateTimeOffset TimestampUtc { get; }

    [JsonPropertyName("is_available")]
    public bool IsAvailable { get; }

    [JsonPropertyName("qualification")]
    public DifferentialSampleQualification Qualification { get; }

    [JsonPropertyName("expected_voltage_v")]
    public double? ExpectedVoltageV { get; }

    [JsonPropertyName("observed_voltage_v")]
    public double? ObservedVoltageV { get; }

    [JsonPropertyName("residual_v")]
    public double? ResidualVolts { get; }

    [JsonPropertyName("excess_droop_v")]
    public double? ExcessDroopVolts { get; }

    [JsonPropertyName("apparent_slope_v_per_unit")]
    public double? ApparentSlopeVoltsPerUnit { get; }

    [JsonPropertyName("detail")]
    public string Detail { get; }

    public DifferentialPrediction(
        DateTimeOffset timestampUtc,
        bool isAvailable,
        DifferentialSampleQualification qualification,
        double? expectedVoltageV,
        double? observedVoltageV,
        double? residualVolts,
        double? excessDroopVolts,
        double? apparentSlopeVoltsPerUnit,
        string detail)
    {
        TimestampUtc = timestampUtc.ToUniversalTime();
        IsAvailable = isAvailable;
        Qualification = qualification ?? throw new ArgumentNullException(nameof(qualification));
        ExpectedVoltageV = expectedVoltageV;
        ObservedVoltageV = observedVoltageV;
        ResidualVolts = residualVolts;
        ExcessDroopVolts = excessDroopVolts;
        ApparentSlopeVoltsPerUnit = apparentSlopeVoltsPerUnit;
        Detail = detail ?? string.Empty;
    }

    public double? ExpectedVoltage => ExpectedVoltageV;
    public double? ExpectedInputVoltageV => ExpectedVoltageV;
    public double? ResidualV => ResidualVolts;
    public double? ExcessDroopV => ExcessDroopVolts;
    public double? ApparentSlope => ApparentSlopeVoltsPerUnit;
    public double? Slope => ApparentSlopeVoltsPerUnit;
}

/// <summary>
/// An immutable fitted artifact.  Coefficients and all qualification settings
/// are copied at fit time; no method updates this object with new observations.
/// </summary>
public sealed record DifferentialModelArtifact
{
    public const int CurrentSchemaVersion = DifferentialModelOptions.CurrentSchemaVersion;

    [JsonPropertyName("schema_version")]
    public int SchemaVersion { get; }

    [JsonPropertyName("algorithm_version")]
    public string AlgorithmVersion { get; }

    [JsonPropertyName("feature_version")]
    public string FeatureVersion { get; }

    [JsonPropertyName("load_proxy")]
    public DifferentialLoadProxy LoadProxy { get; }

    [JsonPropertyName("coefficients")]
    public IReadOnlyList<DifferentialModelCoefficient> Coefficients { get; }

    [JsonPropertyName("intercept_v")]
    public double? InterceptVolts { get; }

    [JsonPropertyName("apparent_slope_v_per_unit")]
    public double? ApparentSlopeVoltsPerUnit { get; }

    [JsonPropertyName("slope_unit")]
    public string SlopeUnit { get; }

    [JsonPropertyName("diagnostics")]
    public DifferentialModelDiagnostics Diagnostics { get; }

    [JsonPropertyName("identity")]
    public DifferentialModelIdentity Identity { get; }

    [JsonPropertyName("settings")]
    public DifferentialModelOptions Settings { get; }

    [JsonPropertyName("training_start_utc")]
    public DateTimeOffset TrainingStartUtc { get; }

    [JsonPropertyName("training_end_utc")]
    public DateTimeOffset TrainingEndUtc { get; }

    [JsonPropertyName("created_at_utc")]
    public DateTimeOffset CreatedAtUtc { get; }

    [JsonPropertyName("artifact_hash")]
    public string ArtifactHash { get; }

    [JsonConstructor]
    public DifferentialModelArtifact(
        int schemaVersion,
        string algorithmVersion,
        string featureVersion,
        DifferentialLoadProxy loadProxy,
        IReadOnlyList<DifferentialModelCoefficient>? coefficients,
        double? interceptVolts,
        double? apparentSlopeVoltsPerUnit,
        string slopeUnit,
        DifferentialModelDiagnostics diagnostics,
        DifferentialModelIdentity identity,
        DifferentialModelOptions settings,
        DateTimeOffset trainingStartUtc,
        DateTimeOffset trainingEndUtc,
        DateTimeOffset createdAtUtc,
        string? artifactHash = null)
    {
        if (schemaVersion != CurrentSchemaVersion)
            throw new NotSupportedException($"Unsupported differential artifact schema {schemaVersion}.");
        if (!string.Equals(algorithmVersion, DifferentialModelOptions.CurrentAlgorithmVersion,
                StringComparison.Ordinal) ||
            !string.Equals(featureVersion, DifferentialModelOptions.CurrentFeatureVersion,
                StringComparison.Ordinal))
            throw new NotSupportedException("The differential artifact algorithm or feature version is unsupported.");
        _ = loadProxy.WireName();
        if (string.IsNullOrWhiteSpace(slopeUnit)) throw new ArgumentException("A slope unit is required.", nameof(slopeUnit));
        if (diagnostics is null) throw new ArgumentNullException(nameof(diagnostics));
        if (identity is null) throw new ArgumentNullException(nameof(identity));
        if (settings is null) throw new ArgumentNullException(nameof(settings));
        settings.Validate();
        if (!identity.Matches(settings.Identity))
            throw new ArgumentException("Artifact identity does not match its settings.", nameof(identity));
        if (trainingStartUtc == default || trainingEndUtc == default || createdAtUtc == default ||
            trainingEndUtc < trainingStartUtc)
            throw new ArgumentException("Artifact timestamps are invalid.");
        ValidateFinite(interceptVolts, nameof(interceptVolts));
        ValidateFinite(apparentSlopeVoltsPerUnit, nameof(apparentSlopeVoltsPerUnit));

        SchemaVersion = schemaVersion;
        AlgorithmVersion = algorithmVersion;
        FeatureVersion = featureVersion;
        LoadProxy = loadProxy;
        Coefficients = new ReadOnlyCollection<DifferentialModelCoefficient>(
            (coefficients ?? Array.Empty<DifferentialModelCoefficient>()).ToList());
        InterceptVolts = interceptVolts;
        ApparentSlopeVoltsPerUnit = apparentSlopeVoltsPerUnit;
        SlopeUnit = slopeUnit.Trim();
        Diagnostics = diagnostics;
        Identity = identity;
        Settings = settings;
        TrainingStartUtc = trainingStartUtc.ToUniversalTime();
        TrainingEndUtc = trainingEndUtc.ToUniversalTime();
        CreatedAtUtc = createdAtUtc.ToUniversalTime();

        var expectedHash = DifferentialModelPersistence.ComputeHash(this);
        ArtifactHash = string.IsNullOrWhiteSpace(artifactHash)
            ? expectedHash
            : artifactHash.Trim().ToUpperInvariant();
        if (!string.Equals(ArtifactHash, expectedHash, StringComparison.OrdinalIgnoreCase))
            throw new FormatException("Differential artifact hash does not match its immutable payload.");
    }

    public bool IsFitted => InterceptVolts.HasValue && Coefficients.Count > 0;
    public bool HasSlope => ApparentSlopeVoltsPerUnit.HasValue;
    public double? Slope => ApparentSlopeVoltsPerUnit;
    public string FeatureSetVersion => FeatureVersion;
    public string Hash => ArtifactHash;
    public IReadOnlyList<DifferentialModelCoefficient> Features => Coefficients;

    public DifferentialPrediction Predict(DifferentialSample sample) =>
        DifferentialModelEvaluator.Predict(this, sample);

    public DifferentialPrediction Evaluate(DifferentialSample sample) => Predict(sample);

    public string ToJson(bool indented = true) =>
        DifferentialModelPersistence.Serialize(this, indented);

    public static DifferentialModelArtifact FromJson(string json) =>
        DifferentialModelPersistence.Deserialize(json);

    public static DifferentialModelFitResult Fit(
        IEnumerable<DifferentialSample> samples,
        DifferentialModelOptions? options = null) =>
        DifferentialModelTrainer.Fit(samples, options);

    static void ValidateFinite(double? value, string name)
    {
        if (value is double number && !double.IsFinite(number))
            throw new ArgumentOutOfRangeException(name);
    }
}

/// <summary>Return value from fitting.  The artifact is always replayable.</summary>
public sealed record DifferentialModelFitResult(
    DifferentialModelArtifact Artifact,
    bool IsSuccess,
    string Detail)
{
    public DifferentialModelArtifact Model => Artifact;
    public DifferentialModelDiagnostics Diagnostics => Artifact.Diagnostics;
    public bool IsUsable => Artifact.IsFitted;
}

/// <summary>
/// Stateful convenience wrapper.  State is replaced only when a caller asks
/// to fit; the artifact itself remains immutable.
/// </summary>
public sealed class DifferentialModel
{
    public DifferentialModelArtifact? Artifact { get; private set; }
    public DifferentialModelArtifact? FrozenArtifact => Artifact;

    public DifferentialModelFitResult Fit(
        IEnumerable<DifferentialSample> samples,
        DifferentialModelOptions? options = null)
    {
        var result = DifferentialModelTrainer.Fit(samples, options);
        Artifact = result.Artifact;
        return result;
    }

    public DifferentialPrediction Predict(DifferentialSample sample) =>
        Artifact is null
            ? DifferentialModelEvaluator.Unavailable(sample,
                DifferentialSampleRejectionReason.MISSING_FEATURE,
                "No differential model artifact has been fitted.")
            : Artifact.Predict(sample);
}

public static class DifferentialModelTrainer
{
    sealed record Accepted(DifferentialSample Sample, DifferentialSampleQualification Qualification,
        double[] Features, double Response);

    public static DifferentialModelFitResult Fit(
        IEnumerable<DifferentialSample> samples,
        DifferentialModelOptions? options = null)
    {
        if (samples is null) throw new ArgumentNullException(nameof(samples));
        var settings = options ?? new DifferentialModelOptions();
        settings.Validate();
        var featureSpecs = BuildFeatureSpecs(settings);
        var input = samples.ToList();
        var rejectionCounts = new Dictionary<DifferentialSampleRejectionReason, int>();
        var accepted = new List<Accepted>();

        foreach (var sample in input)
        {
            if (sample is null)
            {
                AddRejection(rejectionCounts, DifferentialSampleRejectionReason.MISSING_FEATURE);
                continue;
            }
            var qualification = DifferentialModelQualification.Evaluate(sample, settings,
                featureSpecs, envelope: null);
            foreach (var reason in qualification.Reasons)
                AddRejection(rejectionCounts, reason);
            if (!qualification.IsQualified) continue;

            var values = new double[featureSpecs.Count];
            for (int i = 0; i < featureSpecs.Count; i++)
                _ = sample.Features.TryGet(featureSpecs[i].Kind, settings.LoadProxy, out values[i]);
            accepted.Add(new Accepted(sample, qualification, values, sample.InputVoltageV!.Value));
        }

        // Stable ordering makes floating-point accumulation and the resulting
        // hash replayable even if a caller supplied a shuffled capture.
        accepted = accepted
            .OrderBy(x => x.Sample.TimestampUtc.ToUniversalTime())
            .ThenBy(x => x.Response)
            .ThenBy(x => string.Join("|", x.Features.Select(v => v.ToString("R"))))
            .ToList();

        var trainingStart = accepted.Count > 0
            ? accepted[0].Sample.TimestampUtc.ToUniversalTime()
            : input.Where(x => x is not null && x.TimestampUtc != default)
                .Select(x => x.TimestampUtc.ToUniversalTime()).DefaultIfEmpty(DateTimeOffset.UnixEpoch).Min();
        var trainingEnd = accepted.Count > 0
            ? accepted[^1].Sample.TimestampUtc.ToUniversalTime()
            : trainingStart;
        if (trainingEnd < trainingStart) trainingEnd = trainingStart;
        var createdAt = settings.ArtifactCreatedAtUtc?.ToUniversalTime() ?? trainingEnd;
        if (createdAt == default) createdAt = DateTimeOffset.UnixEpoch;

        var ranges = BuildRanges(accepted, featureSpecs);
        var centers = new double[featureSpecs.Count];
        var scales = new double[featureSpecs.Count];
        if (accepted.Count > 0)
        {
            for (int j = 0; j < featureSpecs.Count; j++)
            {
                centers[j] = accepted.Average(x => x.Features[j]);
                double variance = accepted.Sum(x =>
                {
                    double d = x.Features[j] - centers[j];
                    return d * d;
                }) / accepted.Count;
                scales[j] = Math.Sqrt(variance);
                if (!double.IsFinite(scales[j]) || scales[j] < 1e-12) scales[j] = 1;
            }
        }
        else
        {
            Array.Fill(scales, 1);
        }

        double? intercept = null;
        double? slope = null;
        double? condition = null;
        int iterations = 0;
        int rank = 0;
        DifferentialModelState state;
        string detail;
        double[] rawCoefficients = new double[featureSpecs.Count];

        if (accepted.Count < settings.MinimumSamples)
        {
            state = DifferentialModelState.INSUFFICIENT_DATA;
            detail = $"Only {accepted.Count} qualified samples were available; " +
                $"{settings.MinimumSamples} are required for a frozen fit.";
        }
        else
        {
            var normalized = accepted.Select(x => Normalize(x.Features, centers, scales)).ToArray();
            var weights = Enumerable.Repeat(1d, accepted.Count).ToArray();
            double[]? beta = null;
            bool solved = TrySolve(normalized, accepted.Select(x => x.Response).ToArray(),
                weights, out beta, out condition, out rank);
            if (!solved || beta is null)
            {
                state = DifferentialModelState.ILL_CONDITIONED;
                detail = "The feature matrix is rank deficient; coefficients are unavailable.";
            }
            else
            {
                for (int iteration = 1; iteration <= settings.MaximumIterations; iteration++)
                {
                    iterations = iteration;
                    var residuals = Residuals(normalized, accepted, beta);
                    for (int i = 0; i < weights.Length; i++)
                    {
                        double magnitude = Math.Abs(residuals[i]);
                        weights[i] = magnitude <= settings.HuberDeltaVolts || magnitude == 0
                            ? 1
                            : settings.HuberDeltaVolts / magnitude;
                    }
                    if (!TrySolve(normalized, accepted.Select(x => x.Response).ToArray(),
                            weights, out var next, out var nextCondition, out rank) || next is null)
                    {
                        state = DifferentialModelState.ILL_CONDITIONED;
                        detail = "Huber reweighting produced a rank-deficient feature matrix.";
                        beta = null;
                        break;
                    }
                    condition = nextCondition;
                    double change = MaxDifference(beta, next);
                    beta = next;
                    if (change <= settings.ConvergenceTolerance) break;
                }

                if (beta is not null)
                {
                    for (int j = 0; j < rawCoefficients.Length; j++)
                        rawCoefficients[j] = beta[j + 1] / scales[j];
                    intercept = beta[0] - Enumerable.Range(0, rawCoefficients.Length)
                        .Sum(j => rawCoefficients[j] * centers[j]);
                    slope = rawCoefficients.Length > 0 ? rawCoefficients[0] : null;
                    bool ill = condition is not double finiteCondition ||
                        !double.IsFinite(finiteCondition) || finiteCondition > settings.MaximumConditionNumber;
                    state = ill ? DifferentialModelState.ILL_CONDITIONED : DifferentialModelState.FITTED;
                    detail = ill
                        ? $"Fit is retained for replay, but condition number {condition:R} exceeds the " +
                          $"slope limit {settings.MaximumConditionNumber:R}."
                        : "Frozen robust Huber regression fitted from qualified samples.";
                }
                else
                {
                    state = DifferentialModelState.ILL_CONDITIONED;
                    detail = "No finite coefficient vector survived robust fitting.";
                }
            }
        }

        double loadSpan = ranges.Count == 0 ? 0 : ranges[0].Span;
        if (slope.HasValue && (accepted.Count < settings.MinimumSlopeSamples ||
                loadSpan < settings.MinimumLoadSpan ||
                state != DifferentialModelState.FITTED ||
                condition is not double c || c > settings.MaximumConditionNumber))
            slope = null;

        var residualMetrics = intercept.HasValue && accepted.Count > 0
            ? CalculateMetrics(accepted, featureSpecs, centers, scales, rawCoefficients, intercept.Value)
            : new Metrics(null, null, null, null);

        var coefficients = new List<DifferentialModelCoefficient>(featureSpecs.Count);
        for (int i = 0; i < featureSpecs.Count; i++)
        {
            var range = ranges.Count > i ? ranges[i] :
                new DifferentialFeatureRange(0, 0, featureSpecs[i].Unit);
            coefficients.Add(new DifferentialModelCoefficient(featureSpecs[i].Kind,
                featureSpecs[i].Name, featureSpecs[i].Unit, rawCoefficients[i],
                centers[i], scales[i], range));
        }
        var diagnostics = new DifferentialModelDiagnostics(state, input.Count,
            accepted.Count, input.Count - accepted.Count, iterations, rank,
            condition, loadSpan, residualMetrics.Rmse, residualMetrics.MedianAbsolute,
            residualMetrics.MaximumAbsolute, residualMetrics.RobustScale,
            slope.HasValue, rejectionCounts,
            detail);

        var artifact = new DifferentialModelArtifact(
            DifferentialModelOptions.CurrentSchemaVersion,
            DifferentialModelOptions.CurrentAlgorithmVersion,
            DifferentialModelOptions.CurrentFeatureVersion,
            settings.LoadProxy,
            coefficients,
            intercept,
            slope,
            settings.LoadProxy.Unit(),
            diagnostics,
            settings.Identity,
            settings,
            trainingStart,
            trainingEnd,
            createdAt);
        return new DifferentialModelFitResult(artifact,
            artifact.IsFitted, detail);
    }

    static IReadOnlyList<DifferentialModelTrainerFeatureSpec> BuildFeatureSpecs(DifferentialModelOptions settings)
    {
        var result = new List<DifferentialModelTrainerFeatureSpec>
        {
            new(DifferentialFeatureKind.LOAD_PROXY,
                "load_proxy." + settings.LoadProxy.WireName(), settings.LoadProxy.Unit()),
        };
        // Board power is not added twice when it is itself the selected load
        // proxy. This is the explicit one-load-proxy rule.
        if (settings.IncludeBoardPower && settings.LoadProxy != DifferentialLoadProxy.NVML_BOARD_POWER)
            result.Add(new(DifferentialFeatureKind.BOARD_POWER, "board_power", "W"));
        if (settings.IncludeTemperature)
            result.Add(new(DifferentialFeatureKind.TEMPERATURE, "temperature", "C"));
        if (settings.IncludeFanPercent)
            result.Add(new(DifferentialFeatureKind.FAN_PERCENT, "fan_percent", "%"));
        if (settings.IncludeThermalState)
            result.Add(new(DifferentialFeatureKind.THERMAL_STATE, "thermal_state", "code"));
        return result;
    }

    static List<DifferentialFeatureRange> BuildRanges(
        IReadOnlyList<Accepted> accepted, IReadOnlyList<DifferentialModelTrainerFeatureSpec> specs)
    {
        var result = new List<DifferentialFeatureRange>(specs.Count);
        foreach (var (spec, index) in specs.Select((x, i) => (x, i)))
        {
            if (accepted.Count == 0)
            {
                result.Add(new(0, 0, spec.Unit));
                continue;
            }
            double minimum = accepted.Min(x => x.Features[index]);
            double maximum = accepted.Max(x => x.Features[index]);
            result.Add(new(minimum, maximum, spec.Unit));
        }
        return result;
    }

    static double[] Normalize(double[] values, double[] centers, double[] scales)
    {
        var row = new double[values.Length];
        for (int i = 0; i < values.Length; i++) row[i] = (values[i] - centers[i]) / scales[i];
        return row;
    }

    static double[] Residuals(double[][] rows, IReadOnlyList<Accepted> accepted, double[] beta)
    {
        var residuals = new double[rows.Length];
        for (int i = 0; i < rows.Length; i++)
        {
            double predicted = beta[0];
            for (int j = 0; j < rows[i].Length; j++) predicted += beta[j + 1] * rows[i][j];
            residuals[i] = accepted[i].Response - predicted;
        }
        return residuals;
    }

    static bool TrySolve(double[][] rows, double[] response, double[] weights,
        out double[]? solution, out double? conditionNumber, out int rank)
    {
        int dimension = rows[0].Length + 1;
        var matrix = new double[dimension, dimension + 1];
        for (int i = 0; i < rows.Length; i++)
        {
            double weight = weights[i];
            if (!double.IsFinite(weight) || weight <= 0) continue;
            var design = new double[dimension];
            design[0] = 1;
            for (int j = 0; j < rows[i].Length; j++) design[j + 1] = rows[i][j];
            for (int row = 0; row < dimension; row++)
            {
                for (int col = row; col < dimension; col++)
                    matrix[row, col] += weight * design[row] * design[col];
                matrix[row, dimension] += weight * design[row] * response[i];
            }
        }
        for (int row = 0; row < dimension; row++)
            for (int col = 0; col < row; col++) matrix[row, col] = matrix[col, row];

        double largest = 0;
        for (int row = 0; row < dimension; row++)
            for (int col = 0; col < dimension; col++) largest = Math.Max(largest, Math.Abs(matrix[row, col]));
        if (!double.IsFinite(largest) || largest == 0)
        {
            solution = null;
            conditionNumber = null;
            rank = 0;
            return false;
        }

        double smallestPivot = double.PositiveInfinity;
        double largestPivot = 0;
        rank = 0;
        for (int pivot = 0; pivot < dimension; pivot++)
        {
            int best = pivot;
            double bestAbs = Math.Abs(matrix[pivot, pivot]);
            for (int row = pivot + 1; row < dimension; row++)
            {
                double candidate = Math.Abs(matrix[row, pivot]);
                if (candidate > bestAbs) { best = row; bestAbs = candidate; }
            }
            if (!double.IsFinite(bestAbs) || bestAbs <= largest * 1e-12)
                break;
            if (best != pivot)
                for (int col = pivot; col <= dimension; col++)
                    (matrix[pivot, col], matrix[best, col]) = (matrix[best, col], matrix[pivot, col]);
            rank++;
            largestPivot = Math.Max(largestPivot, bestAbs);
            smallestPivot = Math.Min(smallestPivot, bestAbs);
            double denominator = matrix[pivot, pivot];
            for (int row = pivot + 1; row < dimension; row++)
            {
                double factor = matrix[row, pivot] / denominator;
                if (factor == 0) continue;
                matrix[row, pivot] = 0;
                for (int col = pivot + 1; col <= dimension; col++)
                    matrix[row, col] -= factor * matrix[pivot, col];
            }
        }
        conditionNumber = smallestPivot > 0 && double.IsFinite(smallestPivot)
            ? largestPivot / smallestPivot
            : null;
        if (rank < dimension)
        {
            solution = null;
            return false;
        }
        solution = new double[dimension];
        for (int row = dimension - 1; row >= 0; row--)
        {
            double value = matrix[row, dimension];
            for (int col = row + 1; col < dimension; col++) value -= matrix[row, col] * solution[col];
            double denominator = matrix[row, row];
            if (!double.IsFinite(denominator) || Math.Abs(denominator) <= largest * 1e-12)
            {
                solution = null;
                return false;
            }
            solution[row] = value / denominator;
            if (!double.IsFinite(solution[row])) { solution = null; return false; }
        }
        return true;
    }

    static double MaxDifference(double[] left, double[] right)
    {
        double maximum = 0;
        for (int i = 0; i < left.Length; i++) maximum = Math.Max(maximum, Math.Abs(left[i] - right[i]));
        return maximum;
    }

    readonly record struct Metrics(double? Rmse, double? MedianAbsolute,
        double? MaximumAbsolute, double? RobustScale);

    static Metrics CalculateMetrics(IReadOnlyList<Accepted> accepted,
        IReadOnlyList<DifferentialModelTrainerFeatureSpec> specs, double[] centers, double[] scales,
        double[] coefficients, double intercept)
    {
        var absolute = new List<double>(accepted.Count);
        foreach (var item in accepted)
        {
            double expected = intercept;
            for (int i = 0; i < specs.Count; i++) expected += coefficients[i] * item.Features[i];
            double error = item.Response - expected;
            absolute.Add(Math.Abs(error));
        }
        if (absolute.Count == 0) return new(null, null, null, null);
        var ordered = absolute.OrderBy(x => x).ToArray();
        double median = ordered.Length % 2 == 1
            ? ordered[ordered.Length / 2]
            : (ordered[ordered.Length / 2 - 1] + ordered[ordered.Length / 2]) / 2;
        double rmse = Math.Sqrt(absolute.Select(x => x * x).Average());
        // 1.4826 * MAD is a useful persisted diagnostic, not a second fitting
        // pass and not a safety threshold.
        double robustScale = median * 1.4826;
        return new(rmse, median, ordered[^1], robustScale);
    }

    static void AddRejection(Dictionary<DifferentialSampleRejectionReason, int> counts,
        DifferentialSampleRejectionReason reason)
    {
        if (reason == DifferentialSampleRejectionReason.NONE) return;
        counts[reason] = counts.TryGetValue(reason, out var count) ? count + 1 : 1;
    }
}

static class DifferentialModelQualification
{
    public static DifferentialSampleQualification Evaluate(
        DifferentialSample sample,
        DifferentialModelOptions settings,
        IReadOnlyList<DifferentialModelTrainerFeatureSpec> featureSpecs,
        IReadOnlyList<DifferentialModelCoefficient>? envelope)
    {
        var reasons = new List<DifferentialSampleRejectionReason>();
        bool timestampValid = sample.TimestampUtc != default;
        if (!timestampValid) reasons.Add(DifferentialSampleRejectionReason.INVALID_TIMESTAMP);

        bool fresh = sample.IsFresh;
        if (settings.RequireFresh && !sample.IsFresh)
            reasons.Add(DifferentialSampleRejectionReason.NOT_FRESH);
        if (sample.AgeSeconds is double age)
        {
            if (!double.IsFinite(age) || age < 0)
            {
                fresh = false;
                reasons.Add(DifferentialSampleRejectionReason.NON_FINITE);
            }
            else if (age > settings.MaxSampleAgeSeconds)
            {
                fresh = false;
                reasons.Add(DifferentialSampleRejectionReason.AGE_EXCEEDED);
            }
        }

        bool synchronized = sample.IsSynchronized;
        if (settings.RequireSynchronized && !sample.IsSynchronized)
            reasons.Add(DifferentialSampleRejectionReason.NOT_SYNCHRONIZED);
        foreach (var timestamp in new[] { sample.VoltageTimestampUtc, sample.FeatureTimestampUtc })
        {
            if (timestamp is not DateTimeOffset sourceTimestamp) continue;
            double delta = Math.Abs((sourceTimestamp - sample.TimestampUtc).TotalSeconds);
            if (!double.IsFinite(delta) || delta > settings.SynchronizationToleranceSeconds)
            {
                synchronized = false;
                reasons.Add(DifferentialSampleRejectionReason.NOT_SYNCHRONIZED);
            }
        }

        bool settled = sample.IsSettled;
        if (settings.RequireSettled && !sample.IsSettled)
            reasons.Add(DifferentialSampleRejectionReason.NOT_SETTLED);

        bool finite = sample.InputVoltageV is double voltage && double.IsFinite(voltage);
        if (sample.InputVoltageV is null)
            reasons.Add(DifferentialSampleRejectionReason.MISSING_VOLTAGE);
        else if (!finite)
            reasons.Add(DifferentialSampleRejectionReason.NON_FINITE);

        bool inRange = true;
        bool inEnvelope = true;
        foreach (var spec in featureSpecs)
        {
            bool present = sample.Features.TryGet(spec.Kind, settings.LoadProxy, out var value);
            if (!present)
            {
                finite = false;
                reasons.Add(DifferentialSampleRejectionReason.MISSING_FEATURE);
                continue;
            }
            var bounds = settings.Bounds.For(spec.Kind);
            if (bounds is not null && !bounds.Contains(value))
            {
                inRange = false;
                reasons.Add(DifferentialSampleRejectionReason.OUT_OF_RANGE);
            }
            if (envelope is not null)
            {
                var coefficient = envelope.FirstOrDefault(x => x.Feature == spec.Kind);
                if (coefficient is null || !coefficient.Envelope.Contains(value,
                        coefficient.Envelope.Span * settings.EnvelopeMarginFraction))
                {
                    inEnvelope = false;
                    reasons.Add(DifferentialSampleRejectionReason.OUTSIDE_ENVELOPE);
                }
            }
        }

        if (settings.RejectIdentityMismatch && !settings.Identity.IsUnspecified)
        {
            if (string.IsNullOrWhiteSpace(sample.Identity) ||
                !string.Equals(sample.Identity, settings.Identity.CanonicalKey,
                    StringComparison.Ordinal))
            {
                reasons.Add(DifferentialSampleRejectionReason.IDENTITY_MISMATCH);
            }
        }

        var distinct = reasons.Distinct().ToArray();
        bool qualified = timestampValid && fresh && synchronized && finite && inRange &&
            settled && inEnvelope && distinct.Length == 0;
        return new DifferentialSampleQualification(qualified, fresh, synchronized, finite,
            inRange, settled, inEnvelope, distinct,
            qualified ? "Fresh, synchronized, finite, in-range, settled, in-envelope sample."
                : string.Join("; ", distinct.Select(x => x.ToString())));
    }
}

// The trainer's feature type is intentionally internal to this file, but the
// qualification helper needs a stable shape without exposing implementation
// details in the public API.
sealed record DifferentialModelTrainerFeatureSpec(
    DifferentialFeatureKind Kind, string Name, string Unit);

static class DifferentialModelEvaluator
{
    public static DifferentialPrediction Predict(
        DifferentialModelArtifact artifact, DifferentialSample sample)
    {
        if (artifact is null) throw new ArgumentNullException(nameof(artifact));
        if (sample is null) throw new ArgumentNullException(nameof(sample));
        var specs = artifact.Coefficients
            .Select(x => new DifferentialModelTrainerFeatureSpec(x.Feature, x.Name, x.Unit))
            .ToList();
        var qualification = DifferentialModelQualification.Evaluate(sample, artifact.Settings,
            specs, artifact.Coefficients);
        if (!qualification.IsQualified)
            return Unavailable(sample, qualification, "The observation did not pass every model qualification gate.");
        if (!artifact.InterceptVolts.HasValue || artifact.Coefficients.Count == 0)
            return Unavailable(sample, qualification, "This artifact has no fitted coefficient vector.");

        double expected = artifact.InterceptVolts.Value;
        foreach (var coefficient in artifact.Coefficients)
        {
            if (!sample.Features.TryGet(coefficient.Feature, artifact.LoadProxy, out var value))
                return Unavailable(sample, new DifferentialSampleQualification(false,
                    qualification.IsFresh, qualification.IsSynchronized, false,
                    qualification.IsInRange, qualification.IsSettled,
                    qualification.IsInEnvelope,
                    new[] { DifferentialSampleRejectionReason.MISSING_FEATURE },
                    "A selected feature is unavailable at prediction time."),
                    "A selected feature is unavailable at prediction time.");
            expected += coefficient.CoefficientVoltsPerUnit * value;
        }
        double observed = sample.InputVoltageV!.Value;
        double residual = observed - expected;
        double excess = Math.Max(0, -residual - artifact.Settings.ExcessDroopAllowanceVolts);
        return new DifferentialPrediction(sample.TimestampUtc, true, qualification,
            expected, observed, residual, excess, artifact.ApparentSlopeVoltsPerUnit,
            "Expected input voltage and signed residual evaluated from the frozen artifact.");
    }

    public static DifferentialPrediction Unavailable(
        DifferentialSample sample,
        DifferentialSampleRejectionReason reason,
        string detail)
    {
        if (sample is null) throw new ArgumentNullException(nameof(sample));
        return Unavailable(sample, new DifferentialSampleQualification(false,
            sample.IsFresh, sample.IsSynchronized, false, false, sample.IsSettled,
            false, new[] { reason }, detail), detail);
    }

    public static DifferentialPrediction Unavailable(
        DifferentialSample sample,
        DifferentialSampleQualification qualification,
        string detail) =>
        new(sample.TimestampUtc, false, qualification, null, sample.InputVoltageV,
            null, null, null, detail);
}

public static class DifferentialModelPersistence
{
    public const int CurrentSchemaVersion = DifferentialModelOptions.CurrentSchemaVersion;

    static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = false,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    static readonly JsonSerializerOptions JsonIndented = new(Json)
    {
        WriteIndented = true,
    };

    public static string Serialize(DifferentialModelArtifact artifact, bool indented = true)
    {
        if (artifact is null) throw new ArgumentNullException(nameof(artifact));
        return System.Text.Json.JsonSerializer.Serialize(artifact, indented ? JsonIndented : Json);
    }

    public static DifferentialModelArtifact Deserialize(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) throw new FormatException("Differential artifact JSON is empty.");
        try
        {
            using var document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty("schema_version", out var schema) ||
                schema.ValueKind != JsonValueKind.Number || schema.GetInt32() != CurrentSchemaVersion)
                throw new NotSupportedException("Differential artifact schema is unsupported; no migration is performed.");
            var artifact = System.Text.Json.JsonSerializer.Deserialize<DifferentialModelArtifact>(json, Json);
            if (artifact is null) throw new FormatException("Differential artifact JSON is empty.");
            if (!string.Equals(artifact.ArtifactHash, ComputeHash(artifact), StringComparison.OrdinalIgnoreCase))
                throw new FormatException("Differential artifact hash verification failed.");
            return artifact;
        }
        catch (FormatException) { throw; }
        catch (NotSupportedException) { throw; }
        catch (JsonException ex)
        {
            throw new FormatException("Differential artifact JSON is invalid or unsupported.", ex);
        }
        catch (ArgumentException ex)
        {
            throw new FormatException("Differential artifact failed validation.", ex);
        }
    }

    public static bool TryDeserialize(string? json, out DifferentialModelArtifact? artifact)
    {
        try
        {
            artifact = Deserialize(json ?? string.Empty);
            return true;
        }
        catch (FormatException) { artifact = null; return false; }
        catch (NotSupportedException) { artifact = null; return false; }
        catch (ArgumentException) { artifact = null; return false; }
    }

    internal static string ComputeHash(DifferentialModelArtifact artifact)
    {
        var payload = new
        {
            schema_version = artifact.SchemaVersion,
            algorithm_version = artifact.AlgorithmVersion,
            feature_version = artifact.FeatureVersion,
            load_proxy = artifact.LoadProxy,
            coefficients = artifact.Coefficients,
            intercept_v = artifact.InterceptVolts,
            apparent_slope_v_per_unit = artifact.ApparentSlopeVoltsPerUnit,
            slope_unit = artifact.SlopeUnit,
            diagnostics = artifact.Diagnostics,
            identity = artifact.Identity,
            settings = artifact.Settings,
            training_start_utc = artifact.TrainingStartUtc,
            training_end_utc = artifact.TrainingEndUtc,
            created_at_utc = artifact.CreatedAtUtc,
        };
        string canonical = System.Text.Json.JsonSerializer.Serialize(payload, Json);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }
}
