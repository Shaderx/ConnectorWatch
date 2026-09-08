using System.Text.Json;
using System.Text.Json.Serialization;

namespace ConnectorWatch;

/// <summary>
/// The quality state of an observation supplied to a residual detector.
/// Residuals are only actionable when all of the required quality gates have
/// passed.  A detector never turns an unavailable residual into an alert.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ResidualDetectorStatus
{
    NO_SHIFT_DETECTED,
    SUDDEN_DROOP,
    BASELINE_SHIFT,
    OVERVOLTAGE_SHIFT,
    RESIDUAL_UNAVAILABLE,
    RESIDUAL_GAP,
    RESIDUAL_IDENTITY_MISMATCH,
    RESIDUAL_INVALID,

    Normal = NO_SHIFT_DETECTED,
    FastDroop = SUDDEN_DROOP,
    EwmaDroop = BASELINE_SHIFT,
    Overvoltage = OVERVOLTAGE_SHIFT,
    Unavailable = RESIDUAL_UNAVAILABLE,
    Gap = RESIDUAL_GAP,
    IdentityMismatch = RESIDUAL_IDENTITY_MISMATCH,
    Invalid = RESIDUAL_INVALID,
}

public static class ResidualDetectorStatusExtensions
{
    public static string WireName(this ResidualDetectorStatus status) => status switch
    {
        ResidualDetectorStatus.NO_SHIFT_DETECTED => "NO_SHIFT_DETECTED",
        ResidualDetectorStatus.SUDDEN_DROOP => "SUDDEN_DROOP",
        ResidualDetectorStatus.BASELINE_SHIFT => "BASELINE_SHIFT",
        ResidualDetectorStatus.OVERVOLTAGE_SHIFT => "OVERVOLTAGE_SHIFT",
        ResidualDetectorStatus.RESIDUAL_UNAVAILABLE => "RESIDUAL_UNAVAILABLE",
        ResidualDetectorStatus.RESIDUAL_GAP => "RESIDUAL_GAP",
        ResidualDetectorStatus.RESIDUAL_IDENTITY_MISMATCH => "RESIDUAL_IDENTITY_MISMATCH",
        ResidualDetectorStatus.RESIDUAL_INVALID => "RESIDUAL_INVALID",
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, "Unknown residual detector status."),
    };

    public static bool IsAlert(this ResidualDetectorStatus status) =>
        status is ResidualDetectorStatus.SUDDEN_DROOP or
            ResidualDetectorStatus.BASELINE_SHIFT or
            ResidualDetectorStatus.OVERVOLTAGE_SHIFT;

    public static bool IsUnavailable(this ResidualDetectorStatus status) =>
        status is ResidualDetectorStatus.RESIDUAL_UNAVAILABLE or
            ResidualDetectorStatus.RESIDUAL_GAP or
            ResidualDetectorStatus.RESIDUAL_IDENTITY_MISMATCH or
            ResidualDetectorStatus.RESIDUAL_INVALID;
}

/// <summary>
/// A residual observation is deliberately smaller than a full differential
/// sample.  The expensive model evaluation happens before this contract, and
/// the fast/EWMA detectors only consume the signed residual and its quality
/// metadata.
/// </summary>
public sealed record ResidualObservation
{
    [JsonPropertyName("timestamp_utc")]
    public DateTimeOffset TimestampUtc { get; }

    [JsonPropertyName("residual_v")]
    public double? ResidualVolts { get; }

    [JsonPropertyName("is_available")]
    public bool IsAvailable { get; }

    [JsonPropertyName("is_fresh")]
    public bool IsFresh { get; }

    [JsonPropertyName("is_synchronized")]
    public bool IsSynchronized { get; }

    [JsonPropertyName("is_settled")]
    public bool IsSettled { get; }

    [JsonPropertyName("age_seconds")]
    public double? AgeSeconds { get; }

    [JsonPropertyName("identity")]
    public string? Identity { get; }

    [JsonPropertyName("expected_voltage_v")]
    public double? ExpectedVoltageV { get; }

    [JsonPropertyName("observed_voltage_v")]
    public double? ObservedVoltageV { get; }

    [JsonPropertyName("unavailable_reason")]
    public string? UnavailableReason { get; }

    [JsonConstructor]
    public ResidualObservation(
        DateTimeOffset timestampUtc,
        double? residualVolts,
        bool isAvailable = true,
        bool isFresh = true,
        bool isSynchronized = true,
        bool isSettled = true,
        double? ageSeconds = null,
        string? identity = null,
        double? expectedVoltageV = null,
        double? observedVoltageV = null,
        string? unavailableReason = null)
    {
        TimestampUtc = timestampUtc.ToUniversalTime();
        ResidualVolts = residualVolts;
        IsAvailable = isAvailable;
        IsFresh = isFresh;
        IsSynchronized = isSynchronized;
        IsSettled = isSettled;
        AgeSeconds = ageSeconds;
        Identity = string.IsNullOrWhiteSpace(identity) ? null : identity.Trim();
        ExpectedVoltageV = expectedVoltageV;
        ObservedVoltageV = observedVoltageV;
        UnavailableReason = string.IsNullOrWhiteSpace(unavailableReason)
            ? null
            : unavailableReason.Trim();
    }

    public DateTimeOffset Timestamp => TimestampUtc;
    public double? ResidualV => ResidualVolts;
    public double? Residual => ResidualVolts;

    /// <summary>Projects the existing frozen-model prediction into the small
    /// detector contract without recalculating the regression.</summary>
    public static ResidualObservation FromPrediction(DifferentialPrediction prediction)
    {
        if (prediction is null) throw new ArgumentNullException(nameof(prediction));
        var qualification = prediction.Qualification;
        return new ResidualObservation(
            prediction.TimestampUtc,
            prediction.ResidualVolts,
            prediction.IsAvailable,
            qualification.IsFresh,
            qualification.IsSynchronized,
            qualification.IsSettled,
            null,
            null,
            prediction.ExpectedVoltageV,
            prediction.ObservedVoltageV,
            prediction.IsAvailable ? null : prediction.Detail);
    }

    public static ResidualObservation Available(
        DateTimeOffset timestampUtc,
        double residualVolts,
        string? identity = null) =>
        new(timestampUtc, residualVolts, true, true, true, true,
            null, identity);

    public static ResidualObservation Unavailable(
        DateTimeOffset timestampUtc,
        string? reason = null,
        bool isFresh = false,
        bool isSynchronized = false,
        bool isSettled = false,
        string? identity = null) =>
        new(timestampUtc, null, false, isFresh, isSynchronized, isSettled,
            null, identity, null, null, reason);
}

/// <summary>
/// Shared thresholds and quality gates for residual detector strategies.
/// The values are detector configuration, not electrical safety limits.
/// </summary>
public sealed record ResidualDetectorOptions
{
    public const int CurrentSchemaVersion = 1;
    public const string CurrentAlgorithmVersion = "RESIDUAL_DETECTOR_V1";

    [JsonPropertyName("schema_version")]
    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    [JsonPropertyName("algorithm_version")]
    public string AlgorithmVersion { get; init; } = CurrentAlgorithmVersion;

    [JsonPropertyName("fast_droop_threshold_v")]
    public double FastDroopThresholdVolts { get; init; } = .25;

    [JsonPropertyName("fast_overvoltage_threshold_v")]
    public double? FastOvervoltageThresholdVolts { get; init; }

    [JsonPropertyName("ewma_alpha")]
    public double EwmaAlpha { get; init; } = .25;

    [JsonPropertyName("ewma_droop_threshold_v")]
    public double EwmaDroopThresholdVolts { get; init; } = .15;

    [JsonPropertyName("ewma_overvoltage_threshold_v")]
    public double? EwmaOvervoltageThresholdVolts { get; init; }

    [JsonPropertyName("ewma_recovery_threshold_v")]
    public double EwmaRecoveryThresholdVolts { get; init; } = .05;

    [JsonPropertyName("ewma_confirmation_samples")]
    public int EwmaConfirmationSamples { get; init; } = 3;

    [JsonPropertyName("ewma_confirmation_seconds")]
    public double? EwmaConfirmationSeconds { get; init; }

    [JsonPropertyName("maximum_gap_seconds")]
    public double MaximumGapSeconds { get; init; } = 5;

    [JsonPropertyName("maximum_sample_age_seconds")]
    public double? MaximumSampleAgeSeconds { get; init; }

    [JsonPropertyName("require_fresh")]
    public bool RequireFresh { get; init; } = true;

    [JsonPropertyName("require_synchronized")]
    public bool RequireSynchronized { get; init; } = true;

    [JsonPropertyName("require_settled")]
    public bool RequireSettled { get; init; } = true;

    [JsonPropertyName("reset_on_unavailable")]
    public bool ResetOnUnavailable { get; init; } = true;

    [JsonPropertyName("expected_identity")]
    public string? ExpectedIdentity { get; init; }

    // Aliases make the contract convenient for callers that use the naming
    // already present in Config and DifferentialModelOptions.
    [JsonIgnore]
    public double SuddenDroopVolts
    {
        get => FastDroopThresholdVolts;
        init => FastDroopThresholdVolts = value;
    }

    [JsonIgnore]
    public double ShiftVolts
    {
        get => EwmaDroopThresholdVolts;
        init => EwmaDroopThresholdVolts = value;
    }

    [JsonIgnore]
    public int SustainSamples
    {
        get => EwmaConfirmationSamples;
        init => EwmaConfirmationSamples = value;
    }

    [JsonIgnore]
    public double Alpha
    {
        get => EwmaAlpha;
        init => EwmaAlpha = value;
    }

    public void Validate()
    {
        if (SchemaVersion != CurrentSchemaVersion)
            throw new NotSupportedException($"Unsupported residual detector schema {SchemaVersion}.");
        if (!string.Equals(AlgorithmVersion, CurrentAlgorithmVersion,
                StringComparison.Ordinal))
            throw new NotSupportedException($"Unsupported residual detector algorithm '{AlgorithmVersion}'.");
        PositiveFinite(FastDroopThresholdVolts, nameof(FastDroopThresholdVolts));
        OptionalPositiveFinite(FastOvervoltageThresholdVolts,
            nameof(FastOvervoltageThresholdVolts));
        if (!double.IsFinite(EwmaAlpha) || EwmaAlpha <= 0 || EwmaAlpha > 1)
            throw new ArgumentOutOfRangeException(nameof(EwmaAlpha),
                "EWMA alpha must be greater than zero and no greater than one.");
        PositiveFinite(EwmaDroopThresholdVolts, nameof(EwmaDroopThresholdVolts));
        OptionalPositiveFinite(EwmaOvervoltageThresholdVolts,
            nameof(EwmaOvervoltageThresholdVolts));
        if (!double.IsFinite(EwmaRecoveryThresholdVolts) || EwmaRecoveryThresholdVolts < 0 ||
            EwmaRecoveryThresholdVolts >= EwmaDroopThresholdVolts)
            throw new ArgumentOutOfRangeException(nameof(EwmaRecoveryThresholdVolts),
                "EWMA recovery threshold must be non-negative and below the droop threshold.");
        if (EwmaConfirmationSamples < 1)
            throw new ArgumentOutOfRangeException(nameof(EwmaConfirmationSamples));
        OptionalNonNegativeFinite(EwmaConfirmationSeconds,
            nameof(EwmaConfirmationSeconds));
        PositiveFinite(MaximumGapSeconds, nameof(MaximumGapSeconds));
        OptionalNonNegativeFinite(MaximumSampleAgeSeconds,
            nameof(MaximumSampleAgeSeconds));
        if (ExpectedIdentity is not null && string.IsNullOrWhiteSpace(ExpectedIdentity))
            throw new ArgumentException("Expected identity cannot be blank.", nameof(ExpectedIdentity));
    }

    static void PositiveFinite(double value, string name)
    {
        if (!double.IsFinite(value) || value <= 0)
            throw new ArgumentOutOfRangeException(name, "Value must be finite and positive.");
    }

    static void OptionalPositiveFinite(double? value, string name)
    {
        if (value is double number && (!double.IsFinite(number) || number <= 0))
            throw new ArgumentOutOfRangeException(name, "Value must be finite and positive when supplied.");
    }

    static void OptionalNonNegativeFinite(double? value, string name)
    {
        if (value is double number && (!double.IsFinite(number) || number < 0))
            throw new ArgumentOutOfRangeException(name,
                "Value must be finite and non-negative when supplied.");
    }
}

/// <summary>One detector decision and its optional filtered residual.</summary>
public sealed record ResidualDetectorResult
{
    [JsonPropertyName("timestamp_utc")]
    public DateTimeOffset TimestampUtc { get; }

    [JsonPropertyName("detector")]
    public string Detector { get; }

    [JsonPropertyName("status")]
    public ResidualDetectorStatus Status { get; }

    [JsonPropertyName("is_available")]
    public bool IsAvailable { get; }

    [JsonPropertyName("is_alert")]
    public bool IsAlert { get; }

    [JsonPropertyName("is_fast_alert")]
    public bool IsFastAlert { get; }

    [JsonPropertyName("is_ewma_alert")]
    public bool IsEwmaAlert { get; }

    [JsonPropertyName("residual_v")]
    public double? ResidualVolts { get; }

    [JsonPropertyName("filtered_residual_v")]
    public double? FilteredResidualVolts { get; }

    [JsonPropertyName("excess_droop_v")]
    public double? ExcessDroopVolts { get; }

    [JsonPropertyName("consecutive_samples")]
    public int ConsecutiveSamples { get; }

    [JsonPropertyName("qualified_duration_seconds")]
    public double? QualifiedDurationSeconds { get; }

    [JsonPropertyName("state_reset")]
    public bool StateReset { get; }

    [JsonPropertyName("detail")]
    public string Detail { get; }

    [JsonConstructor]
    public ResidualDetectorResult(
        DateTimeOffset timestampUtc,
        string detector,
        ResidualDetectorStatus status,
        bool isAvailable,
        bool isAlert,
        bool isFastAlert,
        bool isEwmaAlert,
        double? residualVolts,
        double? filteredResidualVolts,
        double? excessDroopVolts,
        int consecutiveSamples,
        double? qualifiedDurationSeconds,
        bool stateReset,
        string detail)
    {
        if (string.IsNullOrWhiteSpace(detector))
            throw new ArgumentException("Detector name is required.", nameof(detector));
        if (!Enum.IsDefined(status)) throw new ArgumentOutOfRangeException(nameof(status));
        if (consecutiveSamples < 0) throw new ArgumentOutOfRangeException(nameof(consecutiveSamples));
        ValidateFinite(residualVolts, nameof(residualVolts));
        ValidateFinite(filteredResidualVolts, nameof(filteredResidualVolts));
        ValidateFinite(excessDroopVolts, nameof(excessDroopVolts));
        ValidateFinite(qualifiedDurationSeconds, nameof(qualifiedDurationSeconds));
        TimestampUtc = timestampUtc.ToUniversalTime();
        Detector = detector.Trim();
        Status = status;
        IsAvailable = isAvailable;
        IsAlert = isAlert;
        IsFastAlert = isFastAlert;
        IsEwmaAlert = isEwmaAlert;
        ResidualVolts = residualVolts;
        FilteredResidualVolts = filteredResidualVolts;
        ExcessDroopVolts = excessDroopVolts;
        ConsecutiveSamples = consecutiveSamples;
        QualifiedDurationSeconds = qualifiedDurationSeconds;
        StateReset = stateReset;
        Detail = detail ?? string.Empty;
    }

    [JsonIgnore]
    public string StatusCode => Status.WireName();

    [JsonIgnore]
    public double? ResidualV => ResidualVolts;

    [JsonIgnore]
    public double? FilteredResidualV => FilteredResidualVolts;

    [JsonIgnore]
    public int SampleCount => ConsecutiveSamples;

    static void ValidateFinite(double? value, string name)
    {
        if (value is double number && !double.IsFinite(number))
            throw new ArgumentOutOfRangeException(name, "Detector values must be finite.");
    }
}

/// <summary>
/// Strategy seam for residual detection.  Implementations may be swapped at
/// runtime or in replay without changing the differential model or caller.
/// Implementations are stateful; call <see cref="Reset"/> after a lifecycle
/// boundary when the caller is not passing a gap observation.
/// </summary>
public interface IResidualDetector
{
    string Name { get; }
    ResidualDetectorOptions Options { get; }
    ResidualDetectorResult Update(ResidualObservation observation);
    ResidualDetectorResult Update(DifferentialPrediction prediction);
    ResidualDetectorResult Observe(ResidualObservation observation);
    ResidualDetectorResult Observe(DifferentialPrediction prediction);
    void Reset();
}

/// <summary>Alias used by integrations that call strategies detectors.</summary>
public interface IResidualDetectorStrategy : IResidualDetector { }

/// <summary>
/// O(1), one-sample residual detector.  It is intended for rapid detection of
/// an abrupt excursion and deliberately has no learned state or persistence
/// delay.  A frozen differential artifact supplies the residual upstream.
/// </summary>
public class FastResidualDetector : IResidualDetectorStrategy
{
    public const string DetectorName = "fast-residual";
    readonly ResidualDetectorOptions _options;
    DateTimeOffset? _lastTimestamp;
    int _qualifiedSamples;

    public FastResidualDetector(ResidualDetectorOptions? options = null)
    {
        _options = options ?? new ResidualDetectorOptions();
        _options.Validate();
    }

    public string Name => DetectorName;
    public ResidualDetectorOptions Options => _options;

    public ResidualDetectorResult Update(DifferentialPrediction prediction) =>
        Update(ResidualObservation.FromPrediction(prediction));

    public ResidualDetectorResult Observe(DifferentialPrediction prediction) => Update(prediction);
    public ResidualDetectorResult Observe(ResidualObservation observation) => Update(observation);

    public ResidualDetectorResult Update(ResidualObservation observation)
    {
        if (observation is null) throw new ArgumentNullException(nameof(observation));
        bool gap = IsGap(observation.TimestampUtc);
        bool identityMismatch = IdentityMismatch(observation);
        bool quality = QualityIsUsable(observation);
        bool reset = gap || identityMismatch || !quality;
        if (reset) _qualifiedSamples = 0;

        _lastTimestamp = observation.TimestampUtc;
        if (identityMismatch)
            return Unavailable(observation, ResidualDetectorStatus.RESIDUAL_IDENTITY_MISMATCH,
                true, "Residual identity does not match the detector identity.");
        if (gap)
            return Unavailable(observation, ResidualDetectorStatus.RESIDUAL_GAP,
                true, "Residual continuity gap; the fast detector discarded prior timing state.");
        if (!quality)
            return Unavailable(observation, UnavailableStatus(observation),
                !observation.IsAvailable || _options.ResetOnUnavailable,
                QualityDetail(observation));

        _qualifiedSamples++;
        double residual = observation.ResidualVolts!.Value;
        bool droop = residual <= -_options.FastDroopThresholdVolts;
        bool overvoltage = _options.FastOvervoltageThresholdVolts is double positive &&
            residual >= positive;
        var status = droop ? ResidualDetectorStatus.SUDDEN_DROOP :
            overvoltage ? ResidualDetectorStatus.OVERVOLTAGE_SHIFT :
            ResidualDetectorStatus.NO_SHIFT_DETECTED;
        double? excess = droop
            ? residual + _options.FastDroopThresholdVolts < 0
                ? -(residual + _options.FastDroopThresholdVolts)
                : 0
            : null;
        return new ResidualDetectorResult(observation.TimestampUtc, Name, status,
            true, droop || overvoltage, droop || overvoltage, false,
            residual, residual, excess, _qualifiedSamples, 0, reset,
            droop ? "Fast residual crossed the configured abrupt-droop threshold."
                : overvoltage ? "Fast residual crossed the configured positive threshold."
                : "Fast residual is inside the configured threshold.");
    }

    public void Reset()
    {
        _lastTimestamp = null;
        _qualifiedSamples = 0;
    }

    bool IsGap(DateTimeOffset timestamp)
    {
        if (timestamp == default || !timestamp.Offset.Equals(TimeSpan.Zero))
        {
            // Non-UTC offsets are valid input after normalization; only the
            // default value is invalid.  This branch keeps the check explicit.
        }
        if (timestamp == default) return false;
        if (_lastTimestamp is not DateTimeOffset previous) return false;
        double elapsed = (timestamp.ToUniversalTime() - previous).TotalSeconds;
        return !double.IsFinite(elapsed) || elapsed < 0 || elapsed > _options.MaximumGapSeconds;
    }

    bool IdentityMismatch(ResidualObservation observation) =>
        _options.ExpectedIdentity is string expected &&
        !string.Equals(expected, observation.Identity, StringComparison.Ordinal);

    bool QualityIsUsable(ResidualObservation observation)
    {
        if (!observation.IsAvailable || observation.ResidualVolts is not double residual ||
            !double.IsFinite(residual)) return false;
        if (_options.RequireFresh && !observation.IsFresh) return false;
        if (_options.RequireSynchronized && !observation.IsSynchronized) return false;
        if (_options.RequireSettled && !observation.IsSettled) return false;
        if (observation.AgeSeconds is double age &&
            (!double.IsFinite(age) || age < 0 ||
             _options.MaximumSampleAgeSeconds is double maximum && age > maximum)) return false;
        return true;
    }

    ResidualDetectorStatus UnavailableStatus(ResidualObservation observation) =>
        !observation.IsAvailable || observation.ResidualVolts is null
            ? ResidualDetectorStatus.RESIDUAL_UNAVAILABLE
            : observation.IsFresh == false || observation.IsSynchronized == false ||
                observation.IsSettled == false
                ? ResidualDetectorStatus.RESIDUAL_INVALID
                : ResidualDetectorStatus.RESIDUAL_UNAVAILABLE;

    static string QualityDetail(ResidualObservation observation)
    {
        if (!observation.IsAvailable)
            return string.IsNullOrWhiteSpace(observation.UnavailableReason)
                ? "Residual is unavailable."
                : observation.UnavailableReason!;
        if (!observation.IsFresh) return "Residual source is not fresh.";
        if (!observation.IsSynchronized) return "Residual source is not synchronized.";
        if (!observation.IsSettled) return "Residual source is not settled.";
        return "Residual failed a finite-value or age gate.";
    }

    ResidualDetectorResult Unavailable(ResidualObservation observation,
        ResidualDetectorStatus status, bool stateReset, string detail) =>
        new(observation.TimestampUtc, Name, status, false, false, false, false,
            observation.ResidualVolts, null, null, 0, null, stateReset, detail);
}

/// <summary>
/// Stateful exponentially weighted residual detector.  The EWMA is updated
/// only with qualified observations.  Missing/stale observations and timing
/// gaps reset the filter, so stale history cannot manufacture a sustained
/// alert after a source outage.
/// </summary>
public class EwmaResidualDetector : IResidualDetectorStrategy
{
    public const string DetectorName = "ewma-residual";
    readonly ResidualDetectorOptions _options;
    DateTimeOffset? _lastTimestamp;
    double? _ewma;
    DateTimeOffset? _belowThresholdSince;
    int _consecutiveThresholdSamples;
    bool _alertLatched;
    bool _alertWasNegative;
    int _qualifiedSamples;

    public EwmaResidualDetector(ResidualDetectorOptions? options = null)
    {
        _options = options ?? new ResidualDetectorOptions();
        _options.Validate();
    }

    public string Name => DetectorName;
    public ResidualDetectorOptions Options => _options;
    public double? CurrentEwma => _ewma;
    public double? Ewma => _ewma;
    public int QualifiedSamples => _qualifiedSamples;

    public ResidualDetectorResult Update(DifferentialPrediction prediction) =>
        Update(ResidualObservation.FromPrediction(prediction));

    public ResidualDetectorResult Observe(DifferentialPrediction prediction) => Update(prediction);
    public ResidualDetectorResult Observe(ResidualObservation observation) => Update(observation);

    public ResidualDetectorResult Update(ResidualObservation observation)
    {
        if (observation is null) throw new ArgumentNullException(nameof(observation));
        bool gap = IsGap(observation.TimestampUtc);
        bool identityMismatch = IdentityMismatch(observation);
        bool quality = QualityIsUsable(observation);
        bool reset = gap || identityMismatch || !quality;

        if (gap || identityMismatch || (_options.ResetOnUnavailable && !quality))
            ResetState();
        _lastTimestamp = observation.TimestampUtc;

        if (identityMismatch)
            return Unavailable(observation, ResidualDetectorStatus.RESIDUAL_IDENTITY_MISMATCH,
                true, "Residual identity does not match the detector identity.");
        if (gap)
            return Unavailable(observation, ResidualDetectorStatus.RESIDUAL_GAP,
                true, "Residual continuity gap; EWMA state was reset.");
        if (!quality)
            return Unavailable(observation, UnavailableStatus(observation),
                !observation.IsAvailable || _options.ResetOnUnavailable,
                QualityDetail(observation));

        double residual = observation.ResidualVolts!.Value;
        _ewma = _ewma is double previous
            ? previous + _options.EwmaAlpha * (residual - previous)
            : residual;
        _qualifiedSamples++;
        bool negativeCandidate = _ewma <= -_options.EwmaDroopThresholdVolts;
        bool positiveCandidate = _options.EwmaOvervoltageThresholdVolts is double positive &&
            _ewma >= positive;
        bool candidate = negativeCandidate || positiveCandidate;
        if (candidate)
        {
            _consecutiveThresholdSamples++;
            _belowThresholdSince ??= observation.TimestampUtc;
        }
        else
        {
            _consecutiveThresholdSamples = 0;
            _belowThresholdSince = null;
        }

        double? duration = _belowThresholdSince is DateTimeOffset since
            ? Math.Max(0, (observation.TimestampUtc - since).TotalSeconds)
            : null;
        bool persistence = _consecutiveThresholdSamples >= _options.EwmaConfirmationSamples &&
            (_options.EwmaConfirmationSeconds is not double seconds ||
             duration is double elapsed && elapsed >= seconds);

        if (_alertLatched)
        {
            bool recovered = _alertWasNegative
                ? _ewma > -_options.EwmaRecoveryThresholdVolts
                : _options.EwmaOvervoltageThresholdVolts is double positiveThreshold &&
                    _ewma < positiveThreshold - _options.EwmaRecoveryThresholdVolts;
            if (recovered)
            {
                _alertLatched = false;
                _alertWasNegative = false;
            }
        }
        if (!_alertLatched && persistence)
        {
            _alertLatched = true;
            _alertWasNegative = negativeCandidate;
        }

        var status = _alertLatched
            ? negativeCandidate || _ewma < 0
                ? ResidualDetectorStatus.BASELINE_SHIFT
                : ResidualDetectorStatus.OVERVOLTAGE_SHIFT
            : ResidualDetectorStatus.NO_SHIFT_DETECTED;
        bool alert = _alertLatched;
        double? excess = _ewma is double filtered && filtered < 0
            ? Math.Max(0, -filtered - _options.EwmaDroopThresholdVolts)
            : null;
        return new ResidualDetectorResult(observation.TimestampUtc, Name, status,
            true, alert, false, alert, residual, _ewma, excess,
            _consecutiveThresholdSamples, duration, reset,
            alert ? "EWMA residual crossed and persisted beyond the configured threshold."
                : "EWMA residual is inside the configured threshold or is still accumulating persistence.");
    }

    public void Reset()
    {
        _lastTimestamp = null;
        ResetState();
    }

    void ResetState()
    {
        _ewma = null;
        _belowThresholdSince = null;
        _consecutiveThresholdSamples = 0;
        _alertLatched = false;
        _alertWasNegative = false;
        _qualifiedSamples = 0;
    }

    bool IsGap(DateTimeOffset timestamp)
    {
        if (timestamp == default) return false;
        if (_lastTimestamp is not DateTimeOffset previous) return false;
        double elapsed = (timestamp.ToUniversalTime() - previous).TotalSeconds;
        return !double.IsFinite(elapsed) || elapsed < 0 || elapsed > _options.MaximumGapSeconds;
    }

    bool IdentityMismatch(ResidualObservation observation) =>
        _options.ExpectedIdentity is string expected &&
        !string.Equals(expected, observation.Identity, StringComparison.Ordinal);

    bool QualityIsUsable(ResidualObservation observation)
    {
        if (!observation.IsAvailable || observation.ResidualVolts is not double residual ||
            !double.IsFinite(residual)) return false;
        if (_options.RequireFresh && !observation.IsFresh) return false;
        if (_options.RequireSynchronized && !observation.IsSynchronized) return false;
        if (_options.RequireSettled && !observation.IsSettled) return false;
        if (observation.AgeSeconds is double age &&
            (!double.IsFinite(age) || age < 0 ||
             _options.MaximumSampleAgeSeconds is double maximum && age > maximum)) return false;
        return true;
    }

    ResidualDetectorStatus UnavailableStatus(ResidualObservation observation) =>
        !observation.IsAvailable || observation.ResidualVolts is null
            ? ResidualDetectorStatus.RESIDUAL_UNAVAILABLE
            : observation.IsFresh == false || observation.IsSynchronized == false ||
                observation.IsSettled == false
                ? ResidualDetectorStatus.RESIDUAL_INVALID
                : ResidualDetectorStatus.RESIDUAL_UNAVAILABLE;

    static string QualityDetail(ResidualObservation observation)
    {
        if (!observation.IsAvailable)
            return string.IsNullOrWhiteSpace(observation.UnavailableReason)
                ? "Residual is unavailable."
                : observation.UnavailableReason!;
        if (!observation.IsFresh) return "Residual source is not fresh.";
        if (!observation.IsSynchronized) return "Residual source is not synchronized.";
        if (!observation.IsSettled) return "Residual source is not settled.";
        return "Residual failed a finite-value or age gate.";
    }

    ResidualDetectorResult Unavailable(ResidualObservation observation,
        ResidualDetectorStatus status, bool stateReset, string detail) =>
        new(observation.TimestampUtc, Name, status, false, false, false, false,
            observation.ResidualVolts, _ewma, null, 0, null, stateReset, detail);
}

/// <summary>
/// Runs a fast and a sustained strategy over the same observation.  The
/// result is additive and keeps each strategy's detailed decision, allowing
/// callers to replace either strategy while preserving one integration seam.
/// </summary>
public sealed record CompositeResidualDetectorResult(
    ResidualDetectorResult Fast,
    ResidualDetectorResult Sustained)
{
    [JsonIgnore]
    public bool IsAvailable => Fast.IsAvailable || Sustained.IsAvailable;

    [JsonIgnore]
    public bool IsAlert => Fast.IsAlert || Sustained.IsAlert;

    [JsonIgnore]
    public ResidualDetectorResult Selected => Fast.IsAlert ? Fast :
        Sustained.IsAlert ? Sustained : Fast.IsAvailable ? Fast : Sustained;

    [JsonIgnore]
    public string Status => Selected.Status.WireName();

    public string ToJson(bool indented = true) =>
        JsonSerializer.Serialize(this, new JsonSerializerOptions
        {
            WriteIndented = indented,
            Converters = { new JsonStringEnumConverter() },
        });
}

/// <summary>A small composition seam for the default fast plus EWMA pair.</summary>
public sealed class CompositeResidualDetector
{
    public IResidualDetector Fast { get; }
    public IResidualDetector Sustained { get; }

    public CompositeResidualDetector(
        IResidualDetector fast,
        IResidualDetector sustained)
    {
        Fast = fast ?? throw new ArgumentNullException(nameof(fast));
        Sustained = sustained ?? throw new ArgumentNullException(nameof(sustained));
    }

    public CompositeResidualDetectorResult Update(ResidualObservation observation) =>
        new(Fast.Update(observation), Sustained.Update(observation));

    public CompositeResidualDetectorResult Update(DifferentialPrediction prediction) =>
        Update(ResidualObservation.FromPrediction(prediction));

    public CompositeResidualDetectorResult Observe(ResidualObservation observation) =>
        Update(observation);

    public CompositeResidualDetectorResult Observe(DifferentialPrediction prediction) =>
        Update(prediction);

    public void Reset()
    {
        Fast.Reset();
        Sustained.Reset();
    }
}

/// <summary>Strategy selection for configuration and offline replay.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ResidualDetectorKind
{
    FAST,
    EWMA,
    COMPOSITE,

    Fast = FAST,
    Ewma = EWMA,
    Composite = COMPOSITE,
}

public static class ResidualDetectorFactory
{
    public static IResidualDetector Create(
        ResidualDetectorKind kind,
        ResidualDetectorOptions? options = null) => kind switch
        {
            ResidualDetectorKind.FAST => new FastResidualDetector(options),
            ResidualDetectorKind.EWMA => new EwmaResidualDetector(options),
            ResidualDetectorKind.COMPOSITE => throw new ArgumentException(
                "Use CreateComposite for the composite strategy.", nameof(kind)),
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown residual detector kind."),
        };

    public static CompositeResidualDetector CreateComposite(
        ResidualDetectorOptions? options = null) =>
        new(new FastResidualDetector(options), new EwmaResidualDetector(options));
}
