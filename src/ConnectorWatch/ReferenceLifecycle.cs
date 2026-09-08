using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ConnectorWatch;

/// <summary>
/// Lifecycle of the reference model.  A learned candidate is never implicitly
/// promoted to the accepted frozen model.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ReferenceLifecycleState
{
    REFERENCE_UNVERIFIED,
    REFERENCE_ACCEPTED,
    REFERENCE_STALE,
    REFERENCE_INVALID,

    Unverified = REFERENCE_UNVERIFIED,
    Accepted = REFERENCE_ACCEPTED,
    Stale = REFERENCE_STALE,
    Invalid = REFERENCE_INVALID,
}

/// <summary>Compatibility is deliberately separate from the lifecycle state:
/// a restart can preserve an accepted model, while a degraded source makes
/// that model temporarily unusable.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ReferenceCompatibility
{
    LEGACY,
    COMPATIBLE,
    MISMATCH,
    DEGRADED,
    RESTART,

    MATCHED = COMPATIBLE,
    IDENTITY_MATCH = COMPATIBLE,
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ReferenceCandidateOrigin
{
    LEARNED,
    LEGACY_MIGRATION,
    RESTART_RELEARN,
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ReferenceArchiveReason
{
    EXPLICIT,
    MIGRATION,
    IDENTITY_MISMATCH,
    RESTART,
    STALE,
    INVALID,
}

/// <summary>
/// Configuration that changes which observations are comparable or how a
/// reference is qualified. Presentation settings such as tray alerts, flush
/// cadence, data paths, and UI leases intentionally do not belong here.
/// </summary>
public sealed record ReferenceQualificationConfig
{
    [JsonPropertyName("bin_watts")] public int BinWatts { get; }
    [JsonPropertyName("min_analysis_watts")] public int MinAnalysisWatts { get; }
    [JsonPropertyName("stable_samples")] public int StableSamples { get; }
    [JsonPropertyName("baseline_samples")] public int BaselineSamples { get; }
    [JsonPropertyName("window_samples")] public int WindowSamples { get; }
    [JsonPropertyName("window_max_age_seconds")] public int WindowMaxAgeSeconds { get; }
    [JsonPropertyName("max_age_seconds")] public double MaxAgeSeconds { get; }
    [JsonPropertyName("sample_seconds")] public double SampleSeconds { get; }
    [JsonPropertyName("shift_volts")] public double ShiftVolts { get; }
    [JsonPropertyName("sudden_droop_volts")] public double SuddenDroopVolts { get; }
    [JsonPropertyName("sustain_samples")] public int SustainSamples { get; }
    // These load-boundary/coarse-confirmation values affect which samples can
    // qualify or invalidate a reference. They are part of model identity,
    // unlike alert, flush, path, and other presentation-only settings.
    [JsonPropertyName("load_boundary_hysteresis_watts")] public double LoadBoundaryHysteresisWatts { get; }
    [JsonPropertyName("coarse_confirmation_seconds")] public double CoarseConfirmationSeconds { get; }
    [JsonPropertyName("coarse_confirmation_samples")] public int CoarseConfirmationSamples { get; }
    [JsonPropertyName("gross_under_voltage_v")] public double? GrossUnderVoltageV { get; }
    [JsonPropertyName("gross_over_voltage_v")] public double? GrossOverVoltageV { get; }

    [JsonConstructor]
    public ReferenceQualificationConfig(
        int binWatts,
        int minAnalysisWatts,
        int stableSamples,
        int baselineSamples,
        int windowSamples,
        int windowMaxAgeSeconds,
        double maxAgeSeconds = 5,
        double sampleSeconds = 1,
        double shiftVolts = .2,
        double suddenDroopVolts = .25,
        int sustainSamples = 1,
        double loadBoundaryHysteresisWatts = 0,
        double coarseConfirmationSeconds = 0,
        int coarseConfirmationSamples = 1,
        double? grossUnderVoltageV = null,
        double? grossOverVoltageV = null)
    {
        BinWatts = binWatts;
        MinAnalysisWatts = minAnalysisWatts;
        StableSamples = stableSamples;
        BaselineSamples = baselineSamples;
        WindowSamples = windowSamples;
        WindowMaxAgeSeconds = windowMaxAgeSeconds;
        MaxAgeSeconds = maxAgeSeconds;
        SampleSeconds = sampleSeconds;
        ShiftVolts = shiftVolts;
        SuddenDroopVolts = suddenDroopVolts;
        SustainSamples = sustainSamples;
        LoadBoundaryHysteresisWatts = loadBoundaryHysteresisWatts;
        CoarseConfirmationSeconds = coarseConfirmationSeconds;
        CoarseConfirmationSamples = coarseConfirmationSamples;
        GrossUnderVoltageV = grossUnderVoltageV;
        GrossOverVoltageV = grossOverVoltageV;
        Validate();
    }

    public void Validate()
    {
        if (BinWatts < 1 || MinAnalysisWatts < 0 || StableSamples < 1 ||
            BaselineSamples < 1 || WindowSamples < 1 || WindowMaxAgeSeconds < 0 ||
            SustainSamples < 1 || CoarseConfirmationSamples < 1)
            throw new ArgumentOutOfRangeException(nameof(BinWatts),
                "Reference qualification sample and load bounds are invalid.");
        if (!double.IsFinite(MaxAgeSeconds) || MaxAgeSeconds < 0 ||
            !double.IsFinite(SampleSeconds) || SampleSeconds < 0 ||
            !double.IsFinite(ShiftVolts) || ShiftVolts < 0 ||
            !double.IsFinite(SuddenDroopVolts) || SuddenDroopVolts < 0 ||
            !double.IsFinite(LoadBoundaryHysteresisWatts) || LoadBoundaryHysteresisWatts < 0 ||
            !double.IsFinite(CoarseConfirmationSeconds) || CoarseConfirmationSeconds < 0 ||
            GrossUnderVoltageV is double grossUnder &&
                (!double.IsFinite(grossUnder) || grossUnder < 0) ||
            GrossOverVoltageV is double grossOver &&
                (!double.IsFinite(grossOver) || grossOver < 0) ||
            GrossUnderVoltageV.HasValue && GrossOverVoltageV.HasValue &&
                GrossUnderVoltageV.Value > GrossOverVoltageV.Value)
            throw new ArgumentOutOfRangeException(nameof(MaxAgeSeconds),
                "Reference qualification durations and thresholds must be finite and nonnegative.");
    }
}

/// <summary>
/// Versioned identity for the electrical/reference model. Only fields that
/// can change comparability are included; presentation-only settings are
/// intentionally absent.
/// </summary>
public sealed record ReferenceIdentity
{
    [JsonPropertyName("identity_version")] public int IdentityVersion { get; }
    [JsonPropertyName("gpu_uuid")] public string GpuUuid { get; }
    [JsonPropertyName("board")] public string Board { get; }
    [JsonPropertyName("driver")] public string Driver { get; }
    [JsonPropertyName("source")] public string Source { get; }
    [JsonPropertyName("abi_profile")] public string AbiProfile { get; }
    [JsonPropertyName("analysis_load_source")] public AnalysisLoadSource AnalysisLoadSource { get; }
    [JsonPropertyName("feature_version")] public string FeatureVersion { get; }
    [JsonPropertyName("model_version")] public string ModelVersion { get; }
    [JsonPropertyName("schema_version")] public int SchemaVersion { get; }
    [JsonPropertyName("qualification")] public ReferenceQualificationConfig Qualification { get; }

    [JsonConstructor]
    public ReferenceIdentity(
        int identityVersion,
        string gpuUuid,
        string board,
        string driver,
        string source,
        string abiProfile,
        AnalysisLoadSource analysisLoadSource,
        string featureVersion,
        string modelVersion,
        int schemaVersion,
        ReferenceQualificationConfig qualification)
    {
        if (identityVersion < 1) throw new ArgumentOutOfRangeException(nameof(identityVersion));
        if (schemaVersion < 1) throw new ArgumentOutOfRangeException(nameof(schemaVersion));
        GpuUuid = NormalizeRequired(gpuUuid, nameof(gpuUuid)).ToUpperInvariant();
        Board = NormalizeRequired(board, nameof(board));
        Driver = NormalizeRequired(driver, nameof(driver));
        Source = NormalizeRequired(source, nameof(source));
        AbiProfile = NormalizeRequired(abiProfile, nameof(abiProfile));
        FeatureVersion = NormalizeRequired(featureVersion, nameof(featureVersion));
        ModelVersion = NormalizeRequired(modelVersion, nameof(modelVersion));
        _ = analysisLoadSource.WireName();
        if (qualification is null) throw new ArgumentNullException(nameof(qualification));
        qualification.Validate();
        IdentityVersion = identityVersion;
        AnalysisLoadSource = analysisLoadSource;
        SchemaVersion = schemaVersion;
        Qualification = qualification;
    }

    public static ReferenceIdentity Create(
        string gpuUuid,
        string board,
        string driver,
        string source,
        string abiProfile,
        AnalysisLoadSource analysisLoadSource,
        string featureVersion,
        string modelVersion,
        int schemaVersion,
        ReferenceQualificationConfig qualification,
        int identityVersion = 1) =>
        new(identityVersion, gpuUuid, board, driver, source, abiProfile,
            analysisLoadSource, featureVersion, modelVersion, schemaVersion, qualification);

    public bool MatchesCritical(ReferenceIdentity? other)
    {
        if (other is null) return false;
        return IdentityVersion == other.IdentityVersion &&
            string.Equals(GpuUuid, other.GpuUuid, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(Board, other.Board, StringComparison.Ordinal) &&
            string.Equals(Driver, other.Driver, StringComparison.Ordinal) &&
            string.Equals(Source, other.Source, StringComparison.Ordinal) &&
            string.Equals(AbiProfile, other.AbiProfile, StringComparison.Ordinal) &&
            AnalysisLoadSource == other.AnalysisLoadSource &&
            string.Equals(FeatureVersion, other.FeatureVersion, StringComparison.Ordinal) &&
            string.Equals(ModelVersion, other.ModelVersion, StringComparison.Ordinal) &&
            SchemaVersion == other.SchemaVersion &&
            Qualification == other.Qualification;
    }

    public bool IsCompatibleWith(ReferenceIdentity? other) => MatchesCritical(other);

    [JsonIgnore]
    public string CanonicalJson => JsonSerializer.Serialize(this, ReferenceJson.Options);

    [JsonIgnore]
    public string Fingerprint => Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes(CanonicalJson)));

    [JsonIgnore]
    public string VersionedKey => $"reference-v{IdentityVersion}:{Fingerprint}";

    static string NormalizeRequired(string value, string name) =>
        string.IsNullOrWhiteSpace(value)
            ? throw new ArgumentException("A reference identity value is required.", name)
            : value.Trim();
}

/// <summary>One candidate bin. A nullable reference is expected while the
/// candidate is still learning; an accepted model may contain only bins with
/// a finite reference value.</summary>
public sealed record ReferenceBinStatistics
{
    [JsonPropertyName("bin_watts")] public int BinWatts { get; }
    [JsonPropertyName("reference_volts")] public double? ReferenceVolts { get; }
    [JsonPropertyName("reference_p05_volts")] public double? ReferenceP05Volts { get; }
    [JsonPropertyName("learning_samples")] public int LearningSamples { get; }
    [JsonPropertyName("observed_samples")] public int ObservedSamples { get; }

    [JsonConstructor]
    public ReferenceBinStatistics(int binWatts,
        double? referenceVolts,
        double? referenceP05Volts,
        int learningSamples = 0,
        int observedSamples = 0)
    {
        if (binWatts < 0 || learningSamples < 0 || observedSamples < 0)
            throw new ArgumentOutOfRangeException(nameof(binWatts));
        ValidateFinite(referenceVolts, nameof(referenceVolts));
        ValidateFinite(referenceP05Volts, nameof(referenceP05Volts));
        BinWatts = binWatts;
        ReferenceVolts = referenceVolts;
        ReferenceP05Volts = referenceP05Volts;
        LearningSamples = learningSamples;
        ObservedSamples = observedSamples;
    }

    public double? Reference => ReferenceVolts;
    public double? ReferenceP05 => ReferenceP05Volts;
    public bool IsQualified => ReferenceVolts.HasValue;

    static void ValidateFinite(double? value, string name)
    {
        if (value is double number && !double.IsFinite(number))
            throw new ArgumentOutOfRangeException(name, "Reference statistics must be finite.");
    }
}

/// <summary>Mutable-in-concept learning output, represented immutably at the
/// contract boundary so accepting it cannot keep a mutable dictionary alias.</summary>
public sealed record ReferenceCandidateModel
{
    [JsonPropertyName("identity")] public ReferenceIdentity Identity { get; }
    [JsonPropertyName("bins")] public IReadOnlyDictionary<int, ReferenceBinStatistics> Bins { get; }
    [JsonPropertyName("origin")] public ReferenceCandidateOrigin Origin { get; }
    [JsonPropertyName("qualified_samples")] public int QualifiedSamples { get; }
    [JsonPropertyName("required_samples")] public int RequiredSamples { get; }
    [JsonPropertyName("first_observed_at_utc")] public DateTimeOffset FirstObservedAtUtc { get; }
    [JsonPropertyName("last_observed_at_utc")] public DateTimeOffset LastObservedAtUtc { get; }
    [JsonPropertyName("is_qualified")] public bool IsQualified { get; }
    [JsonPropertyName("detail")] public string Detail { get; }

    [JsonConstructor]
    public ReferenceCandidateModel(
        ReferenceIdentity identity,
        IReadOnlyDictionary<int, ReferenceBinStatistics> bins,
        ReferenceCandidateOrigin origin,
        int qualifiedSamples,
        int requiredSamples,
        DateTimeOffset firstObservedAtUtc,
        DateTimeOffset lastObservedAtUtc,
        bool isQualified,
        string detail = "")
    {
        if (identity is null) throw new ArgumentNullException(nameof(identity));
        if (bins is null) throw new ArgumentNullException(nameof(bins));
        if (qualifiedSamples < 0 || requiredSamples < 0)
            throw new ArgumentOutOfRangeException(nameof(qualifiedSamples));
        if (lastObservedAtUtc < firstObservedAtUtc)
            throw new ArgumentException("Candidate observation timestamps are reversed.", nameof(lastObservedAtUtc));
        if (!Enum.IsDefined(origin)) throw new ArgumentOutOfRangeException(nameof(origin));
        var copied = CopyBins(bins);
        if (copied.Any(x => x.Key < 0 || x.Value.BinWatts != x.Key))
            throw new ArgumentException("Candidate bin keys must match their nonnegative watt bins.", nameof(bins));
        Identity = identity;
        Bins = copied;
        Origin = origin;
        QualifiedSamples = qualifiedSamples;
        RequiredSamples = requiredSamples;
        FirstObservedAtUtc = firstObservedAtUtc.ToUniversalTime();
        LastObservedAtUtc = lastObservedAtUtc.ToUniversalTime();
        IsQualified = isQualified && copied.Count > 0 && copied.Values.All(x => x.IsQualified);
        Detail = detail ?? string.Empty;
    }

    public bool IsComplete => IsQualified;
    public bool KnownHealthy => false;

    public static ReferenceCandidateModel Learned(
        ReferenceIdentity identity,
        IReadOnlyDictionary<int, ReferenceBinStatistics> bins,
        int qualifiedSamples,
        int requiredSamples,
        DateTimeOffset firstObservedAtUtc,
        DateTimeOffset lastObservedAtUtc,
        bool isQualified,
        string detail = "") =>
        new(identity, bins, ReferenceCandidateOrigin.LEARNED, qualifiedSamples,
            requiredSamples, firstObservedAtUtc, lastObservedAtUtc, isQualified, detail);

    internal static ReferenceCandidateModel LegacyMigration(
        ReferenceIdentity identity,
        IReadOnlyDictionary<int, ReferenceBinStatistics> bins,
        string detail,
        DateTimeOffset migratedAtUtc) =>
        new(identity, bins, ReferenceCandidateOrigin.LEGACY_MIGRATION,
            qualifiedSamples: bins.Values.Sum(x => x.ObservedSamples),
            requiredSamples: 0,
            migratedAtUtc, migratedAtUtc,
            isQualified: bins.Count > 0 && bins.Values.All(x => x.IsQualified), detail);

    static IReadOnlyDictionary<int, ReferenceBinStatistics> CopyBins(
        IReadOnlyDictionary<int, ReferenceBinStatistics> bins) =>
        new ReadOnlyDictionary<int, ReferenceBinStatistics>(
            bins.ToDictionary(x => x.Key, x => x.Value));
}

/// <summary>Accepted reference values are a separate immutable snapshot. No
/// method updates this object as new observations arrive.</summary>
public sealed record AcceptedReferenceModel
{
    [JsonPropertyName("identity")] public ReferenceIdentity Identity { get; }
    [JsonPropertyName("bins")] public IReadOnlyDictionary<int, ReferenceBinStatistics> Bins { get; }
    [JsonPropertyName("accepted_at_utc")] public DateTimeOffset AcceptedAtUtc { get; }
    [JsonPropertyName("accepted_by")] public string AcceptedBy { get; }
    [JsonPropertyName("acceptance_note")] public string AcceptanceNote { get; }

    [JsonConstructor]
    public AcceptedReferenceModel(
        ReferenceIdentity identity,
        IReadOnlyDictionary<int, ReferenceBinStatistics> bins,
        DateTimeOffset acceptedAtUtc,
        string acceptedBy,
        string acceptanceNote = "")
    {
        if (identity is null) throw new ArgumentNullException(nameof(identity));
        if (bins is null) throw new ArgumentNullException(nameof(bins));
        if (bins.Count == 0 || bins.Any(x => x.Key < 0 || x.Value.BinWatts != x.Key || !x.Value.IsQualified))
            throw new ArgumentException("An accepted reference needs finite qualified bins.", nameof(bins));
        Identity = identity;
        Bins = new ReadOnlyDictionary<int, ReferenceBinStatistics>(
            bins.ToDictionary(x => x.Key, x => x.Value));
        AcceptedAtUtc = acceptedAtUtc.ToUniversalTime();
        AcceptedBy = RequireText(acceptedBy, nameof(acceptedBy));
        AcceptanceNote = acceptanceNote ?? string.Empty;
    }

    [JsonIgnore] public bool IsFrozen => true;
    [JsonIgnore] public bool KnownHealthy => false;
    [JsonIgnore] public bool IsKnownHealthy => false;

    public static AcceptedReferenceModel Freeze(ReferenceCandidateModel candidate,
        DateTimeOffset acceptedAtUtc, string acceptedBy, string acceptanceNote = "")
    {
        if (candidate is null) throw new ArgumentNullException(nameof(candidate));
        if (!candidate.IsQualified)
            throw new InvalidOperationException("Only a qualified candidate can be accepted.");
        return new(candidate.Identity, candidate.Bins, acceptedAtUtc, acceptedBy, acceptanceNote);
    }

    static string RequireText(string value, string name) =>
        string.IsNullOrWhiteSpace(value)
            ? throw new ArgumentException("A non-empty value is required.", name)
            : value.Trim();
}

public sealed record ArchivedReferenceModel
{
    [JsonPropertyName("reference")] public AcceptedReferenceModel Reference { get; }
    [JsonPropertyName("archived_at_utc")] public DateTimeOffset ArchivedAtUtc { get; }
    [JsonPropertyName("archived_by")] public string ArchivedBy { get; }
    [JsonPropertyName("reason")] public ReferenceArchiveReason Reason { get; }
    [JsonPropertyName("detail")] public string Detail { get; }

    [JsonConstructor]
    public ArchivedReferenceModel(AcceptedReferenceModel reference,
        DateTimeOffset archivedAtUtc,
        string archivedBy,
        ReferenceArchiveReason reason = ReferenceArchiveReason.EXPLICIT,
        string detail = "")
    {
        Reference = reference ?? throw new ArgumentNullException(nameof(reference));
        ArchivedAtUtc = archivedAtUtc.ToUniversalTime();
        ArchivedBy = RequireText(archivedBy, nameof(archivedBy));
        Reason = reason;
        Detail = detail ?? string.Empty;
        if (!Enum.IsDefined(reason)) throw new ArgumentOutOfRangeException(nameof(reason));
    }

    static string RequireText(string value, string name) =>
        string.IsNullOrWhiteSpace(value)
            ? throw new ArgumentException("A non-empty value is required.", name)
            : value.Trim();
}

/// <summary>Incident acknowledgement is presentation/operations state, not
/// evidence that a reference is accepted or that the hardware is healthy.</summary>
public sealed record IncidentAcknowledgement
{
    [JsonPropertyName("incident_id")] public string IncidentId { get; }
    [JsonPropertyName("status")] public string Status { get; }
    [JsonPropertyName("acknowledged_at_utc")] public DateTimeOffset AcknowledgedAtUtc { get; }
    [JsonPropertyName("acknowledged_by")] public string AcknowledgedBy { get; }
    [JsonPropertyName("note")] public string Note { get; }

    [JsonConstructor]
    public IncidentAcknowledgement(string incidentId, string status,
        DateTimeOffset acknowledgedAtUtc, string acknowledgedBy, string note = "")
    {
        IncidentId = RequireText(incidentId, nameof(incidentId));
        Status = RequireText(status, nameof(status));
        AcknowledgedAtUtc = acknowledgedAtUtc.ToUniversalTime();
        AcknowledgedBy = RequireText(acknowledgedBy, nameof(acknowledgedBy));
        Note = note ?? string.Empty;
    }

    static string RequireText(string value, string name) =>
        string.IsNullOrWhiteSpace(value)
            ? throw new ArgumentException("A non-empty value is required.", name)
            : value.Trim();
}

public sealed record ReferenceStartupContext(
    [property: JsonPropertyName("is_restart")] bool IsRestart = false,
    [property: JsonPropertyName("is_degraded")] bool IsDegraded = false,
    [property: JsonPropertyName("detail")] string Detail = "");

public sealed record ReferenceStatusSnapshot(
    [property: JsonPropertyName("state")] ReferenceLifecycleState State,
    [property: JsonPropertyName("compatibility")] ReferenceCompatibility Compatibility,
    [property: JsonPropertyName("identity")] ReferenceIdentity Identity,
    [property: JsonPropertyName("candidate")] ReferenceCandidateModel? Candidate,
    [property: JsonPropertyName("accepted")] AcceptedReferenceModel? Accepted,
    [property: JsonPropertyName("archived")] IReadOnlyList<ArchivedReferenceModel> Archived,
    [property: JsonPropertyName("incident_acknowledgements")] IReadOnlyDictionary<string, IncidentAcknowledgement> IncidentAcknowledgements,
    [property: JsonPropertyName("updated_at_utc")] DateTimeOffset UpdatedAtUtc,
    [property: JsonPropertyName("detail")] string Detail)
{
    [JsonIgnore] public bool HasAcceptedReference => Accepted is not null;
    [JsonIgnore] public bool HasCandidate => Candidate is not null;
    [JsonIgnore] public bool CanAnalyze => State == ReferenceLifecycleState.REFERENCE_ACCEPTED &&
        Accepted is not null &&
        (Compatibility is ReferenceCompatibility.COMPATIBLE or ReferenceCompatibility.RESTART);
    [JsonIgnore] public bool KnownHealthy => false;
    public string StateName => State.WireName();
    public string CompatibilityName => Compatibility.WireName();
}

public sealed record ReferencePersistenceDocument
{
    [JsonPropertyName("schema_version")] public int SchemaVersion { get; }
    [JsonPropertyName("state")] public ReferenceLifecycleState State { get; }
    [JsonPropertyName("compatibility")] public ReferenceCompatibility Compatibility { get; }
    [JsonPropertyName("identity")] public ReferenceIdentity? Identity { get; }
    [JsonPropertyName("candidate")] public ReferenceCandidateModel? Candidate { get; }
    [JsonPropertyName("accepted")] public AcceptedReferenceModel? Accepted { get; }
    [JsonPropertyName("archived")] public IReadOnlyList<ArchivedReferenceModel> Archived { get; }
    [JsonPropertyName("incident_acknowledgements")] public IReadOnlyDictionary<string, IncidentAcknowledgement> IncidentAcknowledgements { get; }
    [JsonPropertyName("updated_at_utc")] public DateTimeOffset UpdatedAtUtc { get; }
    [JsonPropertyName("detail")] public string Detail { get; }
    [JsonPropertyName("legacy_identity")] public string? LegacyIdentity { get; }
    [JsonPropertyName("requires_explicit_migration")] public bool RequiresExplicitMigration { get; }

    [JsonConstructor]
    public ReferencePersistenceDocument(
        int schemaVersion,
        ReferenceLifecycleState state,
        ReferenceCompatibility compatibility,
        ReferenceIdentity? identity,
        ReferenceCandidateModel? candidate,
        AcceptedReferenceModel? accepted,
        IReadOnlyList<ArchivedReferenceModel>? archived,
        IReadOnlyDictionary<string, IncidentAcknowledgement>? incidentAcknowledgements,
        DateTimeOffset updatedAtUtc,
        string detail = "",
        string? legacyIdentity = null,
        bool requiresExplicitMigration = false)
    {
        if (schemaVersion < 1) throw new ArgumentOutOfRangeException(nameof(schemaVersion));
        if (!Enum.IsDefined(state)) throw new ArgumentOutOfRangeException(nameof(state));
        if (!Enum.IsDefined(compatibility)) throw new ArgumentOutOfRangeException(nameof(compatibility));
        SchemaVersion = schemaVersion;
        State = state;
        Compatibility = compatibility;
        Identity = identity;
        Candidate = candidate;
        Accepted = accepted;
        Archived = new ReadOnlyCollection<ArchivedReferenceModel>(
            (archived ?? Array.Empty<ArchivedReferenceModel>()).ToList());
        IncidentAcknowledgements = new ReadOnlyDictionary<string, IncidentAcknowledgement>(
            new Dictionary<string, IncidentAcknowledgement>(
                incidentAcknowledgements ?? new Dictionary<string, IncidentAcknowledgement>(),
                StringComparer.Ordinal));
        UpdatedAtUtc = updatedAtUtc.ToUniversalTime();
        Detail = detail ?? string.Empty;
        LegacyIdentity = legacyIdentity;
        RequiresExplicitMigration = requiresExplicitMigration;
    }
}

public sealed record ReferenceOperationResult(
    [property: JsonPropertyName("succeeded")] bool Succeeded,
    [property: JsonPropertyName("state")] ReferenceLifecycleState State,
    [property: JsonPropertyName("compatibility")] ReferenceCompatibility Compatibility,
    [property: JsonPropertyName("detail")] string Detail)
{
    public bool Ok => Succeeded;
}

public sealed record ReferenceMigrationResult(
    [property: JsonPropertyName("document")] ReferencePersistenceDocument Document,
    [property: JsonPropertyName("compatibility")] ReferenceCompatibility Compatibility,
    [property: JsonPropertyName("requires_explicit_migration")] bool RequiresExplicitMigration,
    [property: JsonPropertyName("legacy_identity")] string? LegacyIdentity,
    [property: JsonPropertyName("detail")] string Detail)
{
    public ReferenceCandidateModel? Candidate => Document.Candidate;
}

public sealed record ReferenceLoadResult(
    ReferenceLifecycle Lifecycle,
    ReferenceCompatibility Compatibility,
    bool RequiresExplicitMigration,
    string Detail);

public static class ReferenceLifecycleStateExtensions
{
    public static string WireName(this ReferenceLifecycleState state) => state switch
    {
        ReferenceLifecycleState.REFERENCE_UNVERIFIED => "REFERENCE_UNVERIFIED",
        ReferenceLifecycleState.REFERENCE_ACCEPTED => "REFERENCE_ACCEPTED",
        ReferenceLifecycleState.REFERENCE_STALE => "REFERENCE_STALE",
        ReferenceLifecycleState.REFERENCE_INVALID => "REFERENCE_INVALID",
        _ => throw new ArgumentOutOfRangeException(nameof(state), state,
            "Unknown reference lifecycle state."),
    };
}

public static class ReferenceCompatibilityExtensions
{
    public static string WireName(this ReferenceCompatibility compatibility) => compatibility switch
    {
        ReferenceCompatibility.LEGACY => "LEGACY",
        ReferenceCompatibility.COMPATIBLE => "COMPATIBLE",
        ReferenceCompatibility.MISMATCH => "MISMATCH",
        ReferenceCompatibility.DEGRADED => "DEGRADED",
        ReferenceCompatibility.RESTART => "RESTART",
        _ => throw new ArgumentOutOfRangeException(nameof(compatibility), compatibility,
            "Unknown reference compatibility."),
    };
}

public static class ReferenceCompatibilityClassifier
{
    public const int CurrentPersistenceSchemaVersion = 1;

    public static ReferenceCompatibility Classify(
        ReferencePersistenceDocument? persisted,
        ReferenceIdentity current,
        ReferenceStartupContext? context = null)
    {
        if (current is null) throw new ArgumentNullException(nameof(current));
        if (persisted is null)
            return context?.IsRestart == true ? ReferenceCompatibility.RESTART : ReferenceCompatibility.LEGACY;
        if (persisted.SchemaVersion < CurrentPersistenceSchemaVersion || persisted.Identity is null)
            return ReferenceCompatibility.LEGACY;
        if (persisted.SchemaVersion > CurrentPersistenceSchemaVersion)
            return ReferenceCompatibility.MISMATCH;
        if (persisted.Compatibility == ReferenceCompatibility.LEGACY ||
            persisted.RequiresExplicitMigration || persisted.LegacyIdentity is not null)
            return ReferenceCompatibility.LEGACY;

        // Validate every identity-bearing layer. Checking only the accepted
        // snapshot would let a malformed document with a changed root
        // identity appear compatible, and checking only the root would allow
        // an embedded candidate/snapshot from another model to be used.
        if (!current.MatchesCritical(persisted.Identity) ||
            persisted.Candidate is not null &&
                !current.MatchesCritical(persisted.Candidate.Identity) ||
            persisted.Accepted is not null &&
                !current.MatchesCritical(persisted.Accepted.Identity))
            return ReferenceCompatibility.MISMATCH;
        if (context?.IsDegraded == true) return ReferenceCompatibility.DEGRADED;
        if (context?.IsRestart == true) return ReferenceCompatibility.RESTART;
        return ReferenceCompatibility.COMPATIBLE;
    }
}

/// <summary>
/// Owns candidate/accepted separation, explicit lifecycle operations, and
/// incident acknowledgements. The accepted snapshot is never mutated by
/// candidate updates or acknowledgements.
/// </summary>
public sealed class ReferenceLifecycle
{
    readonly ReferenceIdentity identity;
    readonly List<ArchivedReferenceModel> archived = [];
    readonly Dictionary<string, IncidentAcknowledgement> incidentAcknowledgements =
        new(StringComparer.Ordinal);
    ReferenceCandidateModel? candidate;
    AcceptedReferenceModel? accepted;
    ReferenceLifecycleState state;
    ReferenceCompatibility compatibility;
    DateTimeOffset updatedAtUtc;
    string detail;
    string? legacyIdentity;
    bool requiresExplicitMigration;

    public ReferenceLifecycle(ReferenceIdentity identity, DateTimeOffset nowUtc)
    {
        this.identity = identity ?? throw new ArgumentNullException(nameof(identity));
        state = ReferenceLifecycleState.REFERENCE_UNVERIFIED;
        compatibility = ReferenceCompatibility.COMPATIBLE;
        updatedAtUtc = nowUtc.ToUniversalTime();
        detail = "No accepted reference has been explicitly accepted.";
    }

    public ReferenceLifecycle(ReferenceIdentity identity,
        ReferencePersistenceDocument persisted,
        DateTimeOffset nowUtc,
        ReferenceStartupContext? context = null)
        : this(identity, nowUtc)
    {
        if (persisted is null) throw new ArgumentNullException(nameof(persisted));
        Restore(persisted, context ?? new ReferenceStartupContext());
    }

    public static ReferenceLifecycle Load(ReferenceIdentity identity,
        ReferencePersistenceDocument persisted,
        DateTimeOffset nowUtc,
        ReferenceStartupContext? context = null) =>
        new(identity, persisted, nowUtc, context);

    public ReferenceIdentity Identity => identity;
    public ReferenceLifecycleState State => state;
    public ReferenceCompatibility Compatibility => compatibility;
    public ReferenceCandidateModel? Candidate => candidate;
    public AcceptedReferenceModel? Accepted => accepted;
    public DateTimeOffset UpdatedAtUtc => updatedAtUtc;
    public string Detail => detail;
    public IReadOnlyList<ArchivedReferenceModel> Archived =>
        new ReadOnlyCollection<ArchivedReferenceModel>(archived.ToList());

    public ReferenceStatusSnapshot Snapshot()
    {
        var copiedAcks = new ReadOnlyDictionary<string, IncidentAcknowledgement>(
            new Dictionary<string, IncidentAcknowledgement>(incidentAcknowledgements,
                StringComparer.Ordinal));
        return new(state, compatibility, identity, candidate, accepted,
            new ReadOnlyCollection<ArchivedReferenceModel>(archived.ToList()),
            copiedAcks, updatedAtUtc, detail);
    }

    public ReferencePersistenceDocument ToDocument() =>
        new(ReferenceCompatibilityClassifier.CurrentPersistenceSchemaVersion,
            state, compatibility, identity, candidate, accepted, archived,
            incidentAcknowledgements, updatedAtUtc, detail, legacyIdentity,
            requiresExplicitMigration);

    /// <summary>Publishes a newly learned candidate without changing the
    /// accepted model or accepting the candidate implicitly.</summary>
    public ReferenceOperationResult SetCandidate(ReferenceCandidateModel next)
    {
        if (next is null) throw new ArgumentNullException(nameof(next));
        if (!identity.MatchesCritical(next.Identity))
            return Fail("Candidate identity does not match the current model identity.");
        // Keep an imported legacy candidate visibly in migration state while
        // allowing refreshed observations to update its statistics. The
        // operator must still use migrate-reference (or the explicit flag) to
        // promote it.
        candidate = candidate?.Origin == ReferenceCandidateOrigin.LEGACY_MIGRATION &&
            next.Origin == ReferenceCandidateOrigin.LEARNED
            ? new ReferenceCandidateModel(next.Identity, next.Bins,
                ReferenceCandidateOrigin.LEGACY_MIGRATION, next.QualifiedSamples,
                next.RequiredSamples, next.FirstObservedAtUtc,
                next.LastObservedAtUtc, next.IsQualified,
                string.IsNullOrWhiteSpace(next.Detail) ? candidate.Detail : next.Detail)
            : next;
        if (accepted is null && state != ReferenceLifecycleState.REFERENCE_INVALID)
            state = ReferenceLifecycleState.REFERENCE_UNVERIFIED;
        detail = next.IsQualified
            ? "A qualified candidate is available; explicit acceptance is still required."
            : "A candidate is still learning and is not eligible for acceptance.";
        updatedAtUtc = next.LastObservedAtUtc;
        return Success(detail);
    }

    public ReferenceOperationResult AcceptCandidate(
        DateTimeOffset acceptedAtUtc,
        string acceptedBy,
        string acceptanceNote = "",
        bool explicitLegacyMigration = false)
    {
        if (candidate is null) return Fail("No candidate is available for explicit acceptance.");
        if (!identity.MatchesCritical(candidate.Identity))
            return Fail("Candidate identity does not match the current model identity.");
        if (compatibility == ReferenceCompatibility.MISMATCH)
            return Fail("Reference identity is invalid; archive or migrate the persisted model first.");
        if (compatibility == ReferenceCompatibility.DEGRADED)
            return Fail("The reference source is degraded; restore a healthy source before acceptance.");
        if (!candidate.IsQualified)
            return Fail("The candidate has not completed qualification.");
        if (candidate.Origin == ReferenceCandidateOrigin.LEGACY_MIGRATION && !explicitLegacyMigration)
            return Fail("Legacy candidates require an explicit migration acknowledgement.");

        accepted = AcceptedReferenceModel.Freeze(candidate, acceptedAtUtc, acceptedBy, acceptanceNote);
        // The candidate is copied into the immutable accepted snapshot. Keep
        // one authoritative active model so a restart cannot re-publish the
        // just-accepted values as if they were a pending candidate.
        candidate = null;
        state = ReferenceLifecycleState.REFERENCE_ACCEPTED;
        compatibility = ReferenceCompatibility.COMPATIBLE;
        legacyIdentity = null;
        requiresExplicitMigration = false;
        updatedAtUtc = acceptedAtUtc.ToUniversalTime();
        detail = "Reference explicitly accepted and frozen; acceptance does not certify health.";
        return Success(detail);
    }

    public ReferenceOperationResult Accept(
        DateTimeOffset acceptedAtUtc,
        string acceptedBy,
        string acceptanceNote = "",
        bool explicitLegacyMigration = false) =>
        AcceptCandidate(acceptedAtUtc, acceptedBy, acceptanceNote, explicitLegacyMigration);

    /// <summary>Archives and removes the accepted model. The archive object
    /// is returned so callers can persist it using existing file/archive
    /// semantics; no file is deleted by this isolated contract.</summary>
    public ArchivedReferenceModel ArchiveAccepted(
        DateTimeOffset archivedAtUtc,
        string archivedBy,
        ReferenceArchiveReason reason = ReferenceArchiveReason.EXPLICIT,
        string detail = "")
    {
        if (accepted is null)
            throw new InvalidOperationException("There is no accepted reference to archive.");
        var archivedModel = new ArchivedReferenceModel(accepted, archivedAtUtc,
            archivedBy, reason, detail);
        archived.Add(archivedModel);
        accepted = null;
        candidate = null;
        legacyIdentity = null;
        requiresExplicitMigration = false;
        state = ReferenceLifecycleState.REFERENCE_UNVERIFIED;
        compatibility = ReferenceCompatibility.COMPATIBLE;
        updatedAtUtc = archivedAtUtc.ToUniversalTime();
        this.detail = "Accepted reference archived; relearning requires explicit acceptance.";
        return archivedModel;
    }

    public ArchivedReferenceModel Archive(
        DateTimeOffset archivedAtUtc,
        string archivedBy,
        ReferenceArchiveReason reason = ReferenceArchiveReason.EXPLICIT,
        string detail = "") =>
        ArchiveAccepted(archivedAtUtc, archivedBy, reason, detail);

    public ReferenceOperationResult MarkStale(DateTimeOffset atUtc, string reason = "")
    {
        state = ReferenceLifecycleState.REFERENCE_STALE;
        compatibility = ReferenceCompatibility.DEGRADED;
        updatedAtUtc = atUtc.ToUniversalTime();
        detail = string.IsNullOrWhiteSpace(reason)
            ? "Accepted reference is stale or source qualification is degraded."
            : reason.Trim();
        return Success(detail);
    }

    public ReferenceOperationResult MarkInvalid(DateTimeOffset atUtc, string reason = "")
    {
        state = ReferenceLifecycleState.REFERENCE_INVALID;
        compatibility = ReferenceCompatibility.MISMATCH;
        updatedAtUtc = atUtc.ToUniversalTime();
        detail = string.IsNullOrWhiteSpace(reason)
            ? "Reference identity or persisted model is invalid."
            : reason.Trim();
        return Success(detail);
    }

    public IncidentAcknowledgement AcknowledgeIncident(
        string incidentId,
        string status,
        DateTimeOffset acknowledgedAtUtc,
        string acknowledgedBy,
        string note = "")
    {
        var acknowledgement = new IncidentAcknowledgement(incidentId, status,
            acknowledgedAtUtc, acknowledgedBy, note);
        incidentAcknowledgements[acknowledgement.IncidentId] = acknowledgement;
        return acknowledgement;
    }

    public bool ClearIncidentAcknowledgement(string incidentId)
    {
        if (string.IsNullOrWhiteSpace(incidentId)) return false;
        return incidentAcknowledgements.Remove(incidentId.Trim());
    }

    public bool IsIncidentAcknowledged(string incidentId) =>
        !string.IsNullOrWhiteSpace(incidentId) &&
        incidentAcknowledgements.ContainsKey(incidentId.Trim());

    void Restore(ReferencePersistenceDocument persisted, ReferenceStartupContext context)
    {
        candidate = persisted.Candidate;
        accepted = persisted.Accepted;
        legacyIdentity = persisted.LegacyIdentity;
        requiresExplicitMigration = persisted.RequiresExplicitMigration;
        archived.AddRange(persisted.Archived);
        foreach (var pair in persisted.IncidentAcknowledgements)
            incidentAcknowledgements[pair.Key] = pair.Value;
        compatibility = ReferenceCompatibilityClassifier.Classify(persisted, identity, context);
        updatedAtUtc = persisted.UpdatedAtUtc;
        detail = string.IsNullOrWhiteSpace(context.Detail) ? persisted.Detail : context.Detail;

        if (compatibility == ReferenceCompatibility.MISMATCH)
        {
            state = ReferenceLifecycleState.REFERENCE_INVALID;
            detail = "Persisted reference identity does not match; explicit archive/migration is required.";
            return;
        }
        if (compatibility == ReferenceCompatibility.LEGACY)
        {
            state = ReferenceLifecycleState.REFERENCE_UNVERIFIED;
            detail = "Legacy reference requires explicit migration and acceptance.";
            return;
        }
        if (compatibility == ReferenceCompatibility.DEGRADED)
        {
            state = accepted is null
                ? ReferenceLifecycleState.REFERENCE_UNVERIFIED
                : ReferenceLifecycleState.REFERENCE_STALE;
            return;
        }

        state = persisted.State;
        if (state == ReferenceLifecycleState.REFERENCE_STALE && accepted is not null &&
            compatibility is ReferenceCompatibility.COMPATIBLE or ReferenceCompatibility.RESTART)
        {
            // Stale is a source-health outcome, not a replacement for the
            // accepted snapshot. Once the same identity is observed again on
            // a healthy source, the frozen model can be used without a new
            // acceptance action.
            state = ReferenceLifecycleState.REFERENCE_ACCEPTED;
            detail = "Previously stale reference restored after source recovery; accepted values remain frozen.";
        }
        if (state == ReferenceLifecycleState.REFERENCE_ACCEPTED && accepted is null)
        {
            state = ReferenceLifecycleState.REFERENCE_INVALID;
            compatibility = ReferenceCompatibility.MISMATCH;
            detail = "Persisted state claimed acceptance without an accepted model.";
        }
    }

    ReferenceOperationResult Success(string message) =>
        new(true, state, compatibility, message);

    ReferenceOperationResult Fail(string message) =>
        new(false, state, compatibility, message);
}

public static class ReferencePersistence
{
    public const int CurrentSchemaVersion = ReferenceCompatibilityClassifier.CurrentPersistenceSchemaVersion;

    /// <summary>
    /// Operator-facing migration guidance for the legacy baseline.json shape.
    /// The old file has no versioned identity or acceptance record, so it is
    /// imported as a candidate only; an operator must review it and explicitly
    /// accept it, or archive it and relearn. Importing it never asserts that
    /// the prior reference was known healthy.
    /// </summary>
    public const string LegacyBaselineMigrationNotes =
        "Legacy baseline.json is imported as REFERENCE_UNVERIFIED. " +
        "Review the candidate against the current GPU/source/configuration, " +
        "then explicitly accept it or archive it and relearn; migration never " +
        "asserts that the old reference was known healthy.";

    static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static string Serialize(ReferenceLifecycle lifecycle)
    {
        if (lifecycle is null) throw new ArgumentNullException(nameof(lifecycle));
        return Serialize(lifecycle.ToDocument());
    }

    public static string Serialize(ReferencePersistenceDocument document)
    {
        if (document is null) throw new ArgumentNullException(nameof(document));
        return JsonSerializer.Serialize(document, Options);
    }

    public static ReferencePersistenceDocument Deserialize(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            throw new FormatException("Reference JSON is empty.");
        try
        {
            var document = JsonSerializer.Deserialize<ReferencePersistenceDocument>(json, Options);
            if (document is null) throw new FormatException("Reference JSON was empty.");
            return document;
        }
        catch (JsonException ex)
        {
            throw new FormatException("Reference JSON is invalid or uses an unsupported schema.", ex);
        }
    }

    public static ReferenceLoadResult Load(string? json,
        ReferenceIdentity current,
        DateTimeOffset nowUtc,
        ReferenceStartupContext? context = null)
    {
        if (current is null) throw new ArgumentNullException(nameof(current));
        var effectiveContext = context ?? new ReferenceStartupContext();
        if (string.IsNullOrWhiteSpace(json))
        {
            var emptyCompatibility = effectiveContext.IsRestart
                ? ReferenceCompatibility.RESTART
                : ReferenceCompatibility.LEGACY;
            var emptyDocument = new ReferencePersistenceDocument(
                CurrentSchemaVersion,
                ReferenceLifecycleState.REFERENCE_UNVERIFIED,
                emptyCompatibility,
                current,
                candidate: null,
                accepted: null,
                archived: null,
                incidentAcknowledgements: null,
                nowUtc,
                "No persisted reference model is available.");
            var empty = new ReferenceLifecycle(current, emptyDocument, nowUtc, effectiveContext);
            return new(empty,
                empty.Compatibility,
                false, "No persisted reference model is available.");
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            if (!HasProperty(document.RootElement, "schema_version"))
            {
                var migration = MigrateLegacyBaseline(json, current, nowUtc);
                var migrated = new ReferenceLifecycle(current, migration.Document, nowUtc, effectiveContext);
                return new(migrated, migration.Compatibility,
                    migration.RequiresExplicitMigration, migration.Detail);
            }

            var persisted = Deserialize(json);
            var lifecycle = new ReferenceLifecycle(current, persisted, nowUtc, effectiveContext);
            return new(lifecycle, lifecycle.Compatibility,
                persisted.RequiresExplicitMigration, lifecycle.Detail);
        }
        catch (FormatException)
        {
            var invalid = new ReferenceLifecycle(current, nowUtc);
            invalid.MarkInvalid(nowUtc, "Persisted reference JSON is invalid; explicit archive/relearning is required.");
            return new(invalid, ReferenceCompatibility.MISMATCH, false, invalid.Detail);
        }
        catch (JsonException)
        {
            var invalid = new ReferenceLifecycle(current, nowUtc);
            invalid.MarkInvalid(nowUtc, "Persisted reference JSON is invalid; explicit archive/relearning is required.");
            return new(invalid, ReferenceCompatibility.MISMATCH, false, invalid.Detail);
        }
        catch (ArgumentException)
        {
            var invalid = new ReferenceLifecycle(current, nowUtc);
            invalid.MarkInvalid(nowUtc, "Persisted reference model failed validation; explicit archive/relearning is required.");
            return new(invalid, ReferenceCompatibility.MISMATCH, false, invalid.Detail);
        }
        catch (NotSupportedException)
        {
            var invalid = new ReferenceLifecycle(current, nowUtc);
            invalid.MarkInvalid(nowUtc, "Persisted reference model uses an unsupported schema; explicit archive/relearning is required.");
            return new(invalid, ReferenceCompatibility.MISMATCH, false, invalid.Detail);
        }
    }

    public static ReferenceMigrationResult MigrateLegacyBaseline(
        string json,
        ReferenceIdentity current,
        DateTimeOffset migratedAtUtc)
    {
        if (current is null) throw new ArgumentNullException(nameof(current));
        if (string.IsNullOrWhiteSpace(json)) throw new FormatException("Legacy baseline JSON is empty.");
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
            throw new FormatException("Legacy baseline JSON must be an object.");
        var root = document.RootElement;
        if (!TryGetProperty(root, "Bins", out var binsValue) ||
            binsValue.ValueKind != JsonValueKind.Object)
            throw new FormatException("Legacy baseline JSON has no Bins object.");

        var bins = new Dictionary<int, ReferenceBinStatistics>();
        foreach (var property in binsValue.EnumerateObject())
        {
            if (!int.TryParse(property.Name, out var bin) || bin < 0)
                throw new FormatException("Legacy baseline contains an invalid watt-bin key.");
            if (property.Value.ValueKind != JsonValueKind.Object)
                throw new FormatException("Legacy baseline bin is not an object.");
            var reference = NullableFinite(property.Value, "Reference");
            var p05 = NullableFinite(property.Value, "ReferenceP05");
            var learningSamples = 0;
            if (TryGetProperty(property.Value, "Learning", out var learning) &&
                learning.ValueKind == JsonValueKind.Array)
                learningSamples = learning.GetArrayLength();
            bins[bin] = new ReferenceBinStatistics(bin, reference, p05,
                learningSamples, learningSamples);
        }

        string? legacyIdentity = null;
        if (TryGetProperty(root, "Identity", out var identityValue) &&
            identityValue.ValueKind == JsonValueKind.String)
            legacyIdentity = identityValue.GetString();

        var candidate = bins.Count == 0
            ? null
            : ReferenceCandidateModel.LegacyMigration(current, bins,
                LegacyBaselineMigrationNotes,
                migratedAtUtc);
        var detail = candidate is null
            ? "Legacy baseline had no reference bins; explicit relearning is required."
            : LegacyBaselineMigrationNotes;
        var persisted = new ReferencePersistenceDocument(
            CurrentSchemaVersion,
            ReferenceLifecycleState.REFERENCE_UNVERIFIED,
            ReferenceCompatibility.LEGACY,
            current,
            candidate,
            accepted: null,
            archived: null,
            incidentAcknowledgements: null,
            migratedAtUtc,
            detail,
            legacyIdentity,
            requiresExplicitMigration: true);
        return new(persisted, ReferenceCompatibility.LEGACY, true, legacyIdentity, detail);
    }

    static double? NullableFinite(JsonElement parent, string name)
    {
        if (!TryGetProperty(parent, name, out var value) || value.ValueKind == JsonValueKind.Null)
            return null;
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out var number) ||
            !double.IsFinite(number))
            throw new FormatException("Legacy baseline contains a non-finite reference statistic.");
        return number;
    }

    static bool HasProperty(JsonElement value, string name) =>
        value.ValueKind == JsonValueKind.Object &&
        value.EnumerateObject().Any(x => string.Equals(x.Name, name,
            StringComparison.OrdinalIgnoreCase));

    static bool TryGetProperty(JsonElement value, string name, out JsonElement property)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            foreach (var item in value.EnumerateObject())
            {
                if (string.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    property = item.Value;
                    return true;
                }
            }
        }
        property = default;
        return false;
    }
}

static class ReferenceJson
{
    internal static readonly JsonSerializerOptions Options = new()
    {
        Converters = { new JsonStringEnumConverter() },
    };
}
