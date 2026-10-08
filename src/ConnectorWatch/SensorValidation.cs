using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;

namespace ConnectorWatch;

public sealed class SensorValidationEvidence
{
    public int SchemaVersion { get; set; } = 1;
    public string ResponseProfile { get; set; } = SensorValidationPolicy.ResponseProfile;
    public string TimingProfile { get; set; } = SensorValidationPolicy.TimingProfile;
    public MaintainerOraclePairingProvenance PairingSource { get; set; } = new();
    public SensorValidationThresholds Thresholds { get; set; } = new();
    public SensorResponseMetrics Response { get; set; } = new();
    public SensorTimingMetrics Timing { get; set; } = new();
}

public sealed class SensorValidationThresholds
{
    public int MinimumPairsPerPhase { get; set; } = SensorValidationPolicy.MinimumPairsPerPhase;
    public double MinimumConnectorCurrentRiseA { get; set; } = SensorValidationPolicy.MinimumConnectorCurrentRiseA;
    public double MaximumPairSeparationSeconds { get; set; } = SensorValidationPolicy.MaximumPairSeparationSeconds;
    public double MaximumPollAgeGraceSeconds { get; set; } = SensorValidationPolicy.MaximumPollAgeGraceSeconds;
    public int MinimumNativeSampleCount { get; set; } = SensorValidationPolicy.MinimumNativeSampleCount;
    public double MinimumNativeDurationSeconds { get; set; } = SensorValidationPolicy.MinimumNativeDurationSeconds;
    public double RequestedIntervalSeconds { get; set; } = SensorValidationPolicy.RequestedIntervalSeconds;
    public double MaximumIntervalStandardDeviationFraction { get; set; } = SensorValidationPolicy.MaximumIntervalStandardDeviationFraction;
    public double MaximumIntervalDeviationFraction { get; set; } = SensorValidationPolicy.MaximumIntervalDeviationFraction;
}

public sealed class SensorResponseMetrics
{
    public bool Passed { get; set; }
    public int PassedIndependentComparisons { get; set; }
    public int IdleUniquePairCount { get; set; }
    public int WorkloadUniquePairCount { get; set; }
    public double IdleNativeConnectorCurrentMeanA { get; set; }
    public double WorkloadNativeConnectorCurrentMeanA { get; set; }
    public double NativeConnectorCurrentRiseA { get; set; }
    public double IdleIndependentConnectorCurrentMeanA { get; set; }
    public double WorkloadIndependentConnectorCurrentMeanA { get; set; }
    public double IndependentConnectorCurrentRiseA { get; set; }
}

public sealed class SensorTimingMetrics
{
    public bool Passed { get; set; }
    public int NativeSampleCount { get; set; }
    public double NativeSampleSpanSeconds { get; set; }
    public double NominalIntervalSeconds { get; set; }
    public double MeanIntervalSeconds { get; set; }
    public double IntervalVarianceSecondsSquared { get; set; }
    public double IntervalStandardDeviationSeconds { get; set; }
    public double MaximumAbsoluteIntervalDeviationSeconds { get; set; }
    public double MeanReadDurationMilliseconds { get; set; }
    public double P95ReadDurationMilliseconds { get; set; }
    public double MaximumReadDurationMilliseconds { get; set; }
    public bool PairTimestampsUnique { get; set; }
    public bool PairSeparationWithinLimit { get; set; }
    public bool PollAgeWithinLimit { get; set; }
    public List<SensorPairTimingMetrics> PairingByPhase { get; set; } = [];
}

public sealed class SensorPairTimingMetrics
{
    public string Phase { get; set; } = "";
    public int PairCount { get; set; }
    public double MeanAbsoluteSeparationSeconds { get; set; }
    public double SeparationVarianceSecondsSquared { get; set; }
    public double SeparationStandardDeviationSeconds { get; set; }
    public double MaximumAbsoluteSeparationSeconds { get; set; }
    public double MaximumOraclePollAgeSeconds { get; set; }
    public double MaximumOraclePollPeriodSeconds { get; set; }
    public double MaximumOraclePollAgeLimitSeconds { get; set; }
}

public sealed record SensorValidationSnapshot(
    string EvidenceState,
    string ResponseState,
    string TimingState,
    string TimingBasis,
    double? MeanIntervalSeconds,
    double? IntervalVarianceSecondsSquared,
    double? IntervalStandardDeviationSeconds,
    double? MeanPairSeparationSeconds,
    double? PairSeparationStandardDeviationSeconds,
    double? MaximumPairSeparationSeconds,
    double? MeanReadDurationMilliseconds,
    double? P95ReadDurationMilliseconds,
    double? MaximumReadDurationMilliseconds,
    string EvidenceSha256,
    long CatalogRevision,
    string Detail)
{
    public static SensorValidationSnapshot NotRun(string evidenceSha256 = "", long catalogRevision = 0,
        string detail = "Sensor validation has not been evaluated for this session.") =>
        new("NOT_RUN", "UNKNOWN", "UNKNOWN", "", null, null, null, null, null, null,
            null, null, null, evidenceSha256, catalogRevision, detail);

    public static SensorValidationSnapshot Missing(string evidenceSha256, long catalogRevision,
        string detail = "Validation evidence is unavailable; refresh driver approvals.") =>
        new("MISSING_EVIDENCE", "UNKNOWN", "UNKNOWN", "", null, null, null, null, null, null,
            null, null, null, evidenceSha256, catalogRevision, detail);

    public static SensorValidationSnapshot Rejected(string evidenceSha256, long catalogRevision,
        string detail) =>
        new("REJECTED", "UNKNOWN", "UNKNOWN", "", null, null, null, null, null, null,
            null, null, null, evidenceSha256, catalogRevision, detail);
}

public static class SensorValidationPolicy
{
    public const string ResponseProfile = "IDLE_LOAD_AGREEMENT_V1";
    public const string TimingProfile = "HOST_POLL_AND_ORACLE_PAIRING_V1";
    public const string TimingBasis = "host observation timestamps and independent oracle pairing";
    public const int MinimumPairsPerPhase = 2;
    public const double MinimumConnectorCurrentRiseA = 5.0;
    public const double MaximumPairSeparationSeconds = 1.0;
    public const double MaximumPollAgeGraceSeconds = 1.0;
    public const int MinimumNativeSampleCount = 12;
    public const double MinimumNativeDurationSeconds = 10.0;
    public const double RequestedIntervalSeconds = 1.0;
    public const double MaximumIntervalStandardDeviationFraction = 0.10;
    public const double MaximumIntervalDeviationFraction = 0.50;
    public const int MaximumEvidenceBytes = SignedMetadataVerifier.MaximumPayloadBytes;

    public static SensorValidationEvidence Evaluate(MaintainerValidationScope scope,
        MaintainerComparisonOracle oracle, DirectRailMetadata metadata,
        IReadOnlyList<MaintainerComparisonResult> comparisons,
        IReadOnlyList<MaintainerSampleEvidence> nativeSamples,
        IReadOnlyList<MaintainerOperationEvidence> nativeOperations,
        IReadOnlyList<long> monotonicTimestamps, int requestedIntervalMilliseconds)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(oracle);
        ArgumentNullException.ThrowIfNull(metadata);
        var result = new SensorValidationEvidence();
        if (!HasExpectedSource(oracle))
            throw new InvalidDataException("Oracle pairing source, units, current basis, or sample timestamps are missing.");
        result.PairingSource = oracle.PairingProvenance!;

        var phases = oracle.Phases.ToDictionary(p => p.Name, StringComparer.OrdinalIgnoreCase);
        if (phases.Count != 2 || !phases.ContainsKey("idle") || !phases.ContainsKey("workload"))
            throw new InvalidDataException("Sensor validation requires exact idle and workload phases.");
        var pairs = new Dictionary<string, List<(MaintainerOracleSample Pair, DirectRailSample Native)>>(StringComparer.OrdinalIgnoreCase);
        foreach (var phase in phases.Values)
        {
            var decoded = new List<(MaintainerOracleSample Pair, DirectRailSample Native)>();
            foreach (var pair in phase.Samples)
            {
                if (pair.NativeTimestampUtc is null || pair.IndependentTimestampUtc is null ||
                    pair.SourcePollTimestampUtc is null || string.IsNullOrWhiteSpace(pair.PairId))
                    throw new InvalidDataException("Every oracle pair needs distinct native, independent, and source-poll timestamps.");
                if (pair.SourcePollPeriodMilliseconds is < 1 or > 10_000)
                    throw new InvalidDataException("Oracle source polling period is missing or out of range.");
                if (pair.NativeReturnCode != 0 || pair.GuardStatus != "pass" || pair.TimedOut)
                    throw new InvalidDataException("A paired native response failed its return, guard, or timeout check.");
                var raw = Convert.FromBase64String(pair.StatusResponseBase64);
                decoded.Add((pair, DirectNvRails.DecodeStatus(raw, metadata, pair.NativeTimestampUtc.Value)));
            }
            pairs[phase.Name] = decoded;
        }

        var idle = pairs["idle"];
        var workload = pairs["workload"];
        result.Response.IdleUniquePairCount = idle.Count;
        result.Response.WorkloadUniquePairCount = workload.Count;
        result.Response.PassedIndependentComparisons = comparisons.Count(c => c.Passed);
        result.Response.IdleNativeConnectorCurrentMeanA = Mean(idle.Select(s => s.Native.TwelveVHpwr.Amps));
        result.Response.WorkloadNativeConnectorCurrentMeanA = Mean(workload.Select(s => s.Native.TwelveVHpwr.Amps));
        result.Response.NativeConnectorCurrentRiseA = result.Response.WorkloadNativeConnectorCurrentMeanA -
            result.Response.IdleNativeConnectorCurrentMeanA;
        result.Response.IdleIndependentConnectorCurrentMeanA = Mean(idle.Select(s => s.Pair.TwelveVHpwr.CurrentA));
        result.Response.WorkloadIndependentConnectorCurrentMeanA = Mean(workload.Select(s => s.Pair.TwelveVHpwr.CurrentA));
        result.Response.IndependentConnectorCurrentRiseA = result.Response.WorkloadIndependentConnectorCurrentMeanA -
            result.Response.IdleIndependentConnectorCurrentMeanA;
        result.Response.Passed = comparisons.Count == 4 && comparisons.All(c => c.Passed) &&
            idle.Count >= MinimumPairsPerPhase && workload.Count >= MinimumPairsPerPhase &&
            result.Response.NativeConnectorCurrentRiseA >= MinimumConnectorCurrentRiseA &&
            result.Response.IndependentConnectorCurrentRiseA >= MinimumConnectorCurrentRiseA;

        var timing = result.Timing;
        timing.NativeSampleCount = monotonicTimestamps.Count;
        timing.NominalIntervalSeconds = requestedIntervalMilliseconds / 1000.0;
        var intervals = new List<double>();
        for (var i = 1; i < monotonicTimestamps.Count; i++)
            intervals.Add((monotonicTimestamps[i] - monotonicTimestamps[i - 1]) / (double)Stopwatch.Frequency);
        if (intervals.Any(x => !double.IsFinite(x) || x <= 0))
            throw new InvalidDataException("Native monotonic sample timestamps are not strictly increasing.");
        timing.NativeSampleSpanSeconds = intervals.Sum();
        timing.MeanIntervalSeconds = intervals.Count == 0 ? 0 : Mean(intervals);
        timing.IntervalVarianceSecondsSquared = SampleVariance(intervals);
        timing.IntervalStandardDeviationSeconds = Math.Sqrt(timing.IntervalVarianceSecondsSquared);
        timing.MaximumAbsoluteIntervalDeviationSeconds = intervals.Count == 0 ? 0 :
            intervals.Max(x => Math.Abs(x - RequestedIntervalSeconds));
        var readDurations = nativeOperations.Skip(1).Select(o => (double)o.DurationMilliseconds).ToArray();
        timing.MeanReadDurationMilliseconds = readDurations.Length == 0 ? 0 : Mean(readDurations);
        timing.P95ReadDurationMilliseconds = Percentile95(readDurations);
        timing.MaximumReadDurationMilliseconds = readDurations.Length == 0 ? 0 : readDurations.Max();

        var pairIds = new HashSet<string>(StringComparer.Ordinal);
        var nativeTimes = new HashSet<DateTimeOffset>();
        var independentTimes = new HashSet<DateTimeOffset>();
        var pairUnique = true;
        var pairDeltaOk = true;
        var pollAgeOk = true;
        foreach (var phaseName in new[] { "idle", "workload" })
        {
            var phasePairs = pairs[phaseName];
            var separations = new List<double>();
            var ages = new List<double>();
            var pollPeriods = new List<double>();
            var ageLimits = new List<double>();
            foreach (var sample in phasePairs)
            {
                var pair = sample.Pair;
                var nativeTimestamp = pair.NativeTimestampUtc!.Value;
                var independentTimestamp = pair.IndependentTimestampUtc!.Value;
                var sourcePollTimestamp = pair.SourcePollTimestampUtc!.Value;
                var pollPeriodMilliseconds = pair.SourcePollPeriodMilliseconds!.Value;
                pairUnique &= pairIds.Add(pair.PairId) && nativeTimes.Add(nativeTimestamp) &&
                    independentTimes.Add(independentTimestamp);
                var separation = Math.Abs((nativeTimestamp - independentTimestamp).TotalSeconds);
                var age = (independentTimestamp - sourcePollTimestamp).TotalSeconds;
                var pollLimit = pollPeriodMilliseconds / 1000.0 + MaximumPollAgeGraceSeconds;
                separations.Add(separation);
                ages.Add(age);
                pollPeriods.Add(pollPeriodMilliseconds / 1000.0);
                ageLimits.Add(pollLimit);
                pairDeltaOk &= separation <= MaximumPairSeparationSeconds;
                pollAgeOk &= age >= 0 && age <= pollLimit;
            }
            timing.PairingByPhase.Add(new SensorPairTimingMetrics
            {
                Phase = phaseName,
                PairCount = phasePairs.Count,
                MeanAbsoluteSeparationSeconds = separations.Count == 0 ? 0 : Mean(separations),
                SeparationVarianceSecondsSquared = SampleVariance(separations),
                SeparationStandardDeviationSeconds = Math.Sqrt(SampleVariance(separations)),
                MaximumAbsoluteSeparationSeconds = separations.Count == 0 ? 0 : separations.Max(),
                MaximumOraclePollAgeSeconds = ages.Count == 0 ? 0 : ages.Max(),
                MaximumOraclePollPeriodSeconds = pollPeriods.Count == 0 ? 0 : pollPeriods.Max(),
                MaximumOraclePollAgeLimitSeconds = ageLimits.Count == 0 ? 0 : ageLimits.Max(),
            });
        }
        timing.PairTimestampsUnique = pairUnique;
        timing.PairSeparationWithinLimit = pairDeltaOk;
        timing.PollAgeWithinLimit = pollAgeOk;
        timing.Passed = requestedIntervalMilliseconds == 1000 &&
            timing.NativeSampleCount >= MinimumNativeSampleCount &&
            timing.NativeSampleSpanSeconds >= MinimumNativeDurationSeconds && intervals.Count > 0 &&
            timing.IntervalStandardDeviationSeconds <= RequestedIntervalSeconds * MaximumIntervalStandardDeviationFraction &&
            timing.MaximumAbsoluteIntervalDeviationSeconds <= RequestedIntervalSeconds * MaximumIntervalDeviationFraction &&
            idle.Count >= MinimumPairsPerPhase && workload.Count >= MinimumPairsPerPhase &&
            pairUnique && pairDeltaOk && pollAgeOk;
        return result;
    }

    public static SensorValidationSnapshot ValidateRuntimeEvidence(ReadOnlySpan<byte> evidenceBytes,
        string expectedEvidenceSha256, DriverCatalogEntry entry, string profile,
        string readerVersion, string appVersion, long catalogRevision)
    {
        if (entry is null || !IsDigest(expectedEvidenceSha256) ||
            !expectedEvidenceSha256.Equals(entry.EvidenceSha256, StringComparison.Ordinal))
            return SensorValidationSnapshot.Rejected(expectedEvidenceSha256, catalogRevision,
                "Validation evidence digest does not match the current approved catalog entry.");
        if (evidenceBytes.IsEmpty || evidenceBytes.Length > MaximumEvidenceBytes)
            return SensorValidationSnapshot.Rejected(expectedEvidenceSha256, catalogRevision,
                "Validation evidence is empty or exceeds its size limit.");
        if (!CryptographicOperations.FixedTimeEquals(SHA256.HashData(evidenceBytes), Convert.FromHexString(expectedEvidenceSha256)))
            return SensorValidationSnapshot.Rejected(expectedEvidenceSha256, catalogRevision,
                "Validation evidence digest does not match the currently approved catalog entry.");
        try
        {
            using var document = StrictJson.ParseObject(evidenceBytes, "driver validation evidence");
            var root = document.RootElement;
            if (root.TryGetProperty("attestation_type", out var attestationType))
            {
                if (attestationType.ValueKind != JsonValueKind.String ||
                    attestationType.GetString() != "connectorwatch-bootstrap-driver-approvals")
                    return SensorValidationSnapshot.Rejected(expectedEvidenceSha256, catalogRevision,
                        "Validation evidence contains an unsupported attestation type.");
                var attestation = BootstrapDriverApprovalPolicy.Parse(evidenceBytes);
                var legacyApproval = attestation.Approvals.SingleOrDefault(item =>
                    item.DriverVersion == entry.Identity.DriverVersion);
                bool legacyScope = legacyApproval is not null && profile == DirectNvRails.ReaderProfile &&
                    profile == entry.ReaderProfile &&
                    entry.Identity.VendorId == "10DE" && entry.Identity.DeviceId == "2B85" &&
                    entry.Identity.SubsystemId == "89EE1043" && entry.Identity.Os == "windows" &&
                    entry.Identity.Architecture == "x64" && entry.MinimumReaderVersion == "1.0.0" &&
                    entry.MaximumReaderVersionExclusive == "1.1.0" && entry.MinimumAppVersion == "1.5.0" &&
                    entry.MaximumAppVersionExclusive == "1.6.0" && entry.SupportsReader(readerVersion) &&
                    entry.SupportsApp(appVersion);
                if (!legacyScope)
                    return SensorValidationSnapshot.Rejected(expectedEvidenceSha256, catalogRevision,
                        "Bootstrap validation evidence scope does not match the exact approved driver entry.");
                return new SensorValidationSnapshot("LEGACY", "UNKNOWN", "UNKNOWN", "", null, null, null,
                    null, null, null, null, null, null, expectedEvidenceSha256, catalogRevision,
                    "This approved entry uses the fixed bootstrap attestation and has no measured sensor-validation evidence.");
            }
            var hasSensorValidation = root.TryGetProperty("sensor_validation", out var sensor);
            if (hasSensorValidation)
                StrictJson.RequireOnlyProperties(root, "driver validation evidence", "schema_version", "evidence_type",
                    "run_id", "captured_utc", "scope", "native_operations", "sample_progression",
                    "independent_comparisons", "checks", "outcome", "approval_authority", "sensor_validation");
            else
                StrictJson.RequireOnlyProperties(root, "driver validation evidence", "schema_version", "evidence_type",
                    "run_id", "captured_utc", "scope", "native_operations", "sample_progression",
                    "independent_comparisons", "checks", "outcome", "approval_authority");
            if (StrictJson.RequiredInt32(root, "schema_version") != 1 ||
                StrictJson.RequiredString(root, "evidence_type", 80) != "connectorwatch-driver-validation")
                return SensorValidationSnapshot.Rejected(expectedEvidenceSha256, catalogRevision,
                    "Validation evidence type or schema is unsupported.");
            if (StrictJson.RequiredString(root, "outcome", 80) != "full_validation_passed")
                return SensorValidationSnapshot.Rejected(expectedEvidenceSha256, catalogRevision,
                    "The signed driver validation evidence did not record a full pass.");
            if (!ScopeMatches(root.GetProperty("scope"), entry, profile, readerVersion, appVersion))
                return SensorValidationSnapshot.Rejected(expectedEvidenceSha256, catalogRevision,
                    "Validation evidence scope does not match the current approved driver entry.");
            if (!AllPassed(root.GetProperty("checks")) || !AllFourComparisonsPassed(root.GetProperty("independent_comparisons")))
                return SensorValidationSnapshot.Rejected(expectedEvidenceSha256, catalogRevision,
                    "Validation evidence contains a failed structural or independent comparison check.");

            if (!hasSensorValidation || sensor.ValueKind == JsonValueKind.Null)
                return new SensorValidationSnapshot("LEGACY", "UNKNOWN", "UNKNOWN", "", null, null, null,
                    null, null, null, null, null, null, expectedEvidenceSha256, catalogRevision,
                    "This approved entry predates measured sensor response and timing evidence.");
            var parsed = ParseSensorValidation(sensor);
            return ToSnapshot(parsed, expectedEvidenceSha256, catalogRevision);
        }
        catch (Exception ex) when (ex is InvalidDataException or JsonException or ArgumentException or
                                   FormatException or InvalidOperationException or KeyNotFoundException)
        {
            return SensorValidationSnapshot.Rejected(expectedEvidenceSha256, catalogRevision,
                "Validation evidence could not be verified: " + ex.Message);
        }
    }

    public static SensorValidationSnapshot ValidateStandaloneEvidence(ReadOnlySpan<byte> evidenceBytes,
        string expectedEvidenceSha256)
    {
        if (evidenceBytes.IsEmpty || evidenceBytes.Length > MaximumEvidenceBytes ||
            !IsDigest(expectedEvidenceSha256) ||
            !CryptographicOperations.FixedTimeEquals(SHA256.HashData(evidenceBytes),
                Convert.FromHexString(expectedEvidenceSha256)))
            return SensorValidationSnapshot.Rejected(expectedEvidenceSha256, 0,
                "Validation evidence digest is invalid or does not match its exact bytes.");
        try
        {
            using var document = StrictJson.ParseObject(evidenceBytes, "driver validation evidence");
            var scope = document.RootElement.GetProperty("scope");
            StrictJson.RequireOnlyProperties(scope, "driver validation scope", "vendor_id", "device_id",
                "subsystem_id", "os", "architecture", "driver_version", "reader_profile",
                "reader_version", "app_version", "tool_commit");
            var identity = new DriverIdentity(
                StrictJson.RequiredString(scope, "vendor_id", 4),
                StrictJson.RequiredString(scope, "device_id", 4),
                StrictJson.RequiredString(scope, "subsystem_id", 8),
                StrictJson.RequiredString(scope, "os", 40),
                StrictJson.RequiredString(scope, "architecture", 40),
                StrictJson.RequiredString(scope, "driver_version", 40));
            var profile = StrictJson.RequiredString(scope, "reader_profile", 80);
            var readerVersion = StrictJson.RequiredString(scope, "reader_version", 32);
            var appVersion = StrictJson.RequiredString(scope, "app_version", 32);
            var entry = new DriverCatalogEntry(identity, profile, readerVersion,
                AdvanceMinor(readerVersion), appVersion, AdvanceMajor(appVersion), "approved",
                expectedEvidenceSha256, DateTimeOffset.UtcNow, "evidence validation");
            return ValidateRuntimeEvidence(evidenceBytes, expectedEvidenceSha256, entry, profile,
                readerVersion, appVersion, 0);
        }
        catch (Exception ex) when (ex is InvalidDataException or JsonException or ArgumentException or
                                   FormatException or InvalidOperationException or KeyNotFoundException)
        {
            return SensorValidationSnapshot.Rejected(expectedEvidenceSha256, 0,
                "Validation evidence scope could not be verified: " + ex.Message);
        }
    }

    public static bool IsDigest(string? value) => value is { Length: 64 } &&
        value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

    public static SensorValidationSnapshot SnapshotFromEvidence(ReadOnlySpan<byte> evidenceBytes,
        string expectedEvidenceSha256, DriverCatalogEntry entry, string profile,
        string readerVersion, string appVersion, long catalogRevision) =>
        ValidateRuntimeEvidence(evidenceBytes, expectedEvidenceSha256, entry, profile,
            readerVersion, appVersion, catalogRevision);

    private static SensorValidationSnapshot ToSnapshot(SensorValidationEvidence evidence,
        string hash, long revision)
    {
        bool trustedScope = evidence.SchemaVersion == 1 && evidence.ResponseProfile == ResponseProfile &&
            evidence.TimingProfile == TimingProfile && HasExpectedSource(evidence.PairingSource) &&
            ThresholdsMatch(evidence.Thresholds);
        bool responsePass = trustedScope && evidence.Response.Passed && ResponseMetricsPass(evidence.Response);
        bool timingPass = trustedScope && evidence.Timing.Passed && TimingMetricsPass(evidence.Timing);
        var phasePairs = evidence.Timing.PairingByPhase;
        double? meanSeparation = phasePairs.Count == 2 ? phasePairs.Sum(p => p.MeanAbsoluteSeparationSeconds * p.PairCount) /
            Math.Max(1, phasePairs.Sum(p => p.PairCount)) : null;
        double? separationStdDev = phasePairs.Count == 2 ? Math.Sqrt(Mean(phasePairs.Select(x => x.SeparationVarianceSecondsSquared))) : null;
        double? maxSeparation = phasePairs.Count == 2 ? phasePairs.Max(x => x.MaximumAbsoluteSeparationSeconds) : null;
        var failures = new List<string>();
        if (!trustedScope) failures.Add("sensor validation profile or thresholds are unsupported");
        else
        {
            if (!responsePass) failures.Add(ResponseFailure(evidence.Response));
            if (!timingPass) failures.Add(TimingFailure(evidence.Timing));
        }
        return new SensorValidationSnapshot("VERIFIED", responsePass ? "PASS" : "FAIL",
            timingPass ? "PASS" : "FAIL", TimingBasis,
            evidence.Timing.MeanIntervalSeconds, evidence.Timing.IntervalVarianceSecondsSquared,
            evidence.Timing.IntervalStandardDeviationSeconds, meanSeparation, separationStdDev,
            maxSeparation, evidence.Timing.MeanReadDurationMilliseconds,
            evidence.Timing.P95ReadDurationMilliseconds, evidence.Timing.MaximumReadDurationMilliseconds,
            hash, revision, string.Join("; ", failures));
    }

    private static SensorValidationEvidence ParseSensorValidation(JsonElement element)
    {
        StrictJson.RequireOnlyProperties(element, "sensor validation evidence", "schema_version", "response_profile",
            "timing_profile", "pairing_source", "thresholds", "response", "timing");
        var evidence = new SensorValidationEvidence
        {
            SchemaVersion = StrictJson.RequiredInt32(element, "schema_version"),
            ResponseProfile = StrictJson.RequiredString(element, "response_profile", 80),
            TimingProfile = StrictJson.RequiredString(element, "timing_profile", 80),
            PairingSource = ParsePairingSource(element.GetProperty("pairing_source")),
            Thresholds = ParseThresholds(element.GetProperty("thresholds")),
            Response = ParseResponse(element.GetProperty("response")),
            Timing = ParseTiming(element.GetProperty("timing")),
        };
        return evidence;
    }

    private static MaintainerOraclePairingProvenance ParsePairingSource(JsonElement element)
    {
        StrictJson.RequireOnlyProperties(element, "sensor validation pairing source", "source_name", "sensor_id",
            "pairing_method", "voltage_unit", "power_unit", "current_derivation");
        return new MaintainerOraclePairingProvenance
        {
            SourceName = StrictJson.RequiredString(element, "source_name", 100),
            SensorId = StrictJson.RequiredString(element, "sensor_id", 32),
            PairingMethod = StrictJson.RequiredString(element, "pairing_method", 80),
            VoltageUnit = StrictJson.RequiredString(element, "voltage_unit", 16),
            PowerUnit = StrictJson.RequiredString(element, "power_unit", 16),
            CurrentDerivation = StrictJson.RequiredString(element, "current_derivation", 100),
        };
    }

    private static SensorValidationThresholds ParseThresholds(JsonElement element)
    {
        StrictJson.RequireOnlyProperties(element, "sensor validation thresholds", "minimum_pairs_per_phase",
            "minimum_connector_current_rise_a", "maximum_pair_separation_seconds", "maximum_poll_age_grace_seconds",
            "minimum_native_sample_count", "minimum_native_duration_seconds", "requested_interval_seconds",
            "maximum_interval_standard_deviation_fraction", "maximum_interval_deviation_fraction");
        return new SensorValidationThresholds
        {
            MinimumPairsPerPhase = StrictJson.RequiredInt32(element, "minimum_pairs_per_phase"),
            MinimumConnectorCurrentRiseA = StrictJson.RequiredFiniteDouble(element, "minimum_connector_current_rise_a"),
            MaximumPairSeparationSeconds = StrictJson.RequiredFiniteDouble(element, "maximum_pair_separation_seconds"),
            MaximumPollAgeGraceSeconds = StrictJson.RequiredFiniteDouble(element, "maximum_poll_age_grace_seconds"),
            MinimumNativeSampleCount = StrictJson.RequiredInt32(element, "minimum_native_sample_count"),
            MinimumNativeDurationSeconds = StrictJson.RequiredFiniteDouble(element, "minimum_native_duration_seconds"),
            RequestedIntervalSeconds = StrictJson.RequiredFiniteDouble(element, "requested_interval_seconds"),
            MaximumIntervalStandardDeviationFraction = StrictJson.RequiredFiniteDouble(element, "maximum_interval_standard_deviation_fraction"),
            MaximumIntervalDeviationFraction = StrictJson.RequiredFiniteDouble(element, "maximum_interval_deviation_fraction"),
        };
    }

    private static SensorResponseMetrics ParseResponse(JsonElement element)
    {
        StrictJson.RequireOnlyProperties(element, "sensor response evidence", "passed", "passed_independent_comparisons",
            "idle_unique_pair_count", "workload_unique_pair_count", "idle_native_connector_current_mean_a",
            "workload_native_connector_current_mean_a", "native_connector_current_rise_a",
            "idle_independent_connector_current_mean_a", "workload_independent_connector_current_mean_a",
            "independent_connector_current_rise_a");
        return new SensorResponseMetrics
        {
            Passed = StrictJson.RequiredBoolean(element, "passed"),
            PassedIndependentComparisons = StrictJson.RequiredInt32(element, "passed_independent_comparisons"),
            IdleUniquePairCount = StrictJson.RequiredInt32(element, "idle_unique_pair_count"),
            WorkloadUniquePairCount = StrictJson.RequiredInt32(element, "workload_unique_pair_count"),
            IdleNativeConnectorCurrentMeanA = StrictJson.RequiredFiniteDouble(element, "idle_native_connector_current_mean_a"),
            WorkloadNativeConnectorCurrentMeanA = StrictJson.RequiredFiniteDouble(element, "workload_native_connector_current_mean_a"),
            NativeConnectorCurrentRiseA = StrictJson.RequiredFiniteDouble(element, "native_connector_current_rise_a"),
            IdleIndependentConnectorCurrentMeanA = StrictJson.RequiredFiniteDouble(element, "idle_independent_connector_current_mean_a"),
            WorkloadIndependentConnectorCurrentMeanA = StrictJson.RequiredFiniteDouble(element, "workload_independent_connector_current_mean_a"),
            IndependentConnectorCurrentRiseA = StrictJson.RequiredFiniteDouble(element, "independent_connector_current_rise_a"),
        };
    }

    private static SensorTimingMetrics ParseTiming(JsonElement element)
    {
        StrictJson.RequireOnlyProperties(element, "sensor timing evidence", "passed", "native_sample_count",
            "native_sample_span_seconds", "nominal_interval_seconds", "mean_interval_seconds",
            "interval_variance_seconds_squared", "interval_standard_deviation_seconds",
            "maximum_absolute_interval_deviation_seconds", "mean_read_duration_milliseconds",
            "p95_read_duration_milliseconds", "maximum_read_duration_milliseconds",
            "pair_timestamps_unique", "pair_separation_within_limit", "poll_age_within_limit", "pairing_by_phase");
        var phases = element.GetProperty("pairing_by_phase");
        if (phases.ValueKind != JsonValueKind.Array || phases.GetArrayLength() != 2)
            throw new InvalidDataException("Sensor timing evidence must contain idle and workload pairing metrics.");
        var timing = new SensorTimingMetrics
        {
            Passed = StrictJson.RequiredBoolean(element, "passed"),
            NativeSampleCount = StrictJson.RequiredInt32(element, "native_sample_count"),
            NativeSampleSpanSeconds = StrictJson.RequiredFiniteDouble(element, "native_sample_span_seconds"),
            NominalIntervalSeconds = StrictJson.RequiredFiniteDouble(element, "nominal_interval_seconds"),
            MeanIntervalSeconds = StrictJson.RequiredFiniteDouble(element, "mean_interval_seconds"),
            IntervalVarianceSecondsSquared = StrictJson.RequiredFiniteDouble(element, "interval_variance_seconds_squared"),
            IntervalStandardDeviationSeconds = StrictJson.RequiredFiniteDouble(element, "interval_standard_deviation_seconds"),
            MaximumAbsoluteIntervalDeviationSeconds = StrictJson.RequiredFiniteDouble(element, "maximum_absolute_interval_deviation_seconds"),
            MeanReadDurationMilliseconds = StrictJson.RequiredFiniteDouble(element, "mean_read_duration_milliseconds"),
            P95ReadDurationMilliseconds = StrictJson.RequiredFiniteDouble(element, "p95_read_duration_milliseconds"),
            MaximumReadDurationMilliseconds = StrictJson.RequiredFiniteDouble(element, "maximum_read_duration_milliseconds"),
            PairTimestampsUnique = StrictJson.RequiredBoolean(element, "pair_timestamps_unique"),
            PairSeparationWithinLimit = StrictJson.RequiredBoolean(element, "pair_separation_within_limit"),
            PollAgeWithinLimit = StrictJson.RequiredBoolean(element, "poll_age_within_limit"),
        };
        foreach (var phase in phases.EnumerateArray())
        {
            StrictJson.RequireOnlyProperties(phase, "sensor pair phase metrics", "phase", "pair_count",
                "mean_absolute_separation_seconds", "separation_variance_seconds_squared",
                "separation_standard_deviation_seconds", "maximum_absolute_separation_seconds",
                "maximum_oracle_poll_age_seconds", "maximum_oracle_poll_period_seconds",
                "maximum_oracle_poll_age_limit_seconds");
            timing.PairingByPhase.Add(new SensorPairTimingMetrics
            {
                Phase = StrictJson.RequiredString(phase, "phase", 20),
                PairCount = StrictJson.RequiredInt32(phase, "pair_count"),
                MeanAbsoluteSeparationSeconds = StrictJson.RequiredFiniteDouble(phase, "mean_absolute_separation_seconds"),
                SeparationVarianceSecondsSquared = StrictJson.RequiredFiniteDouble(phase, "separation_variance_seconds_squared"),
                SeparationStandardDeviationSeconds = StrictJson.RequiredFiniteDouble(phase, "separation_standard_deviation_seconds"),
                MaximumAbsoluteSeparationSeconds = StrictJson.RequiredFiniteDouble(phase, "maximum_absolute_separation_seconds"),
                MaximumOraclePollAgeSeconds = StrictJson.RequiredFiniteDouble(phase, "maximum_oracle_poll_age_seconds"),
                MaximumOraclePollPeriodSeconds = StrictJson.RequiredFiniteDouble(phase, "maximum_oracle_poll_period_seconds"),
                MaximumOraclePollAgeLimitSeconds = StrictJson.RequiredFiniteDouble(phase, "maximum_oracle_poll_age_limit_seconds"),
            });
        }
        if (timing.PairingByPhase.Select(p => p.Phase).ToHashSet(StringComparer.OrdinalIgnoreCase)
                .SetEquals(["idle", "workload"]) == false)
            throw new InvalidDataException("Sensor timing evidence phase names must be idle and workload.");
        return timing;
    }

    private static bool ScopeMatches(JsonElement scope, DriverCatalogEntry entry, string profile,
        string readerVersion, string appVersion)
    {
        StrictJson.RequireOnlyProperties(scope, "driver validation scope", "vendor_id", "device_id",
            "subsystem_id", "os", "architecture", "driver_version", "reader_profile",
            "reader_version", "app_version", "tool_commit");
        var evidenceReaderVersion = StrictJson.RequiredString(scope, "reader_version", 32);
        var evidenceAppVersion = StrictJson.RequiredString(scope, "app_version", 32);
        return StrictJson.RequiredString(scope, "vendor_id", 4) == entry.Identity.VendorId &&
            StrictJson.RequiredString(scope, "device_id", 4) == entry.Identity.DeviceId &&
            StrictJson.RequiredString(scope, "subsystem_id", 8) == entry.Identity.SubsystemId &&
            StrictJson.RequiredString(scope, "os", 40) == entry.Identity.Os &&
            StrictJson.RequiredString(scope, "architecture", 40) == entry.Identity.Architecture &&
            StrictJson.RequiredString(scope, "driver_version", 40) == entry.Identity.DriverVersion &&
            StrictJson.RequiredString(scope, "reader_profile", 80) == profile && profile == entry.ReaderProfile &&
            entry.SupportsReader(evidenceReaderVersion) && entry.SupportsReader(readerVersion) &&
            entry.SupportsApp(evidenceAppVersion) && entry.SupportsApp(appVersion) &&
            IsCommit(StrictJson.RequiredString(scope, "tool_commit", 40));
    }

    private static bool AllPassed(JsonElement array)
    {
        if (array.ValueKind != JsonValueKind.Array || array.GetArrayLength() == 0) return false;
        return array.EnumerateArray().All(item => item.ValueKind == JsonValueKind.Object &&
            StrictJson.RequiredBoolean(item, "passed"));
    }

    private static bool AllFourComparisonsPassed(JsonElement array) => array.ValueKind == JsonValueKind.Array &&
        array.GetArrayLength() == 4 && AllPassed(array);

    private static bool HasExpectedSource(MaintainerComparisonOracle oracle) =>
        oracle.PairingProvenance is not null && HasExpectedSource(oracle.PairingProvenance);

    private static bool HasExpectedSource(MaintainerOraclePairingProvenance source) =>
        source.SourceName == "HWiNFO Shared Memory" &&
        source.SensorId.Equals("E0002000", StringComparison.OrdinalIgnoreCase) &&
        source.PairingMethod == "nearest_unused_by_read_timestamp" &&
        source.VoltageUnit == "V" && source.PowerUnit == "W" &&
        source.CurrentDerivation == "rail_power_divided_by_rail_voltage";

    private static bool ThresholdsMatch(SensorValidationThresholds value) =>
        value.MinimumPairsPerPhase == MinimumPairsPerPhase &&
        NearlyEqual(value.MinimumConnectorCurrentRiseA, MinimumConnectorCurrentRiseA) &&
        NearlyEqual(value.MaximumPairSeparationSeconds, MaximumPairSeparationSeconds) &&
        NearlyEqual(value.MaximumPollAgeGraceSeconds, MaximumPollAgeGraceSeconds) &&
        value.MinimumNativeSampleCount == MinimumNativeSampleCount &&
        NearlyEqual(value.MinimumNativeDurationSeconds, MinimumNativeDurationSeconds) &&
        NearlyEqual(value.RequestedIntervalSeconds, RequestedIntervalSeconds) &&
        NearlyEqual(value.MaximumIntervalStandardDeviationFraction, MaximumIntervalStandardDeviationFraction) &&
        NearlyEqual(value.MaximumIntervalDeviationFraction, MaximumIntervalDeviationFraction);

    private static bool ResponseMetricsPass(SensorResponseMetrics value) =>
        value.PassedIndependentComparisons == 4 &&
        value.IdleUniquePairCount >= MinimumPairsPerPhase && value.WorkloadUniquePairCount >= MinimumPairsPerPhase &&
        value.NativeConnectorCurrentRiseA >= MinimumConnectorCurrentRiseA &&
        value.IndependentConnectorCurrentRiseA >= MinimumConnectorCurrentRiseA &&
        NearlyEqual(value.NativeConnectorCurrentRiseA,
            value.WorkloadNativeConnectorCurrentMeanA - value.IdleNativeConnectorCurrentMeanA) &&
        NearlyEqual(value.IndependentConnectorCurrentRiseA,
            value.WorkloadIndependentConnectorCurrentMeanA - value.IdleIndependentConnectorCurrentMeanA);

    private static bool TimingMetricsPass(SensorTimingMetrics value)
    {
        if (value.NativeSampleCount < MinimumNativeSampleCount ||
            value.NativeSampleSpanSeconds < MinimumNativeDurationSeconds ||
            !NearlyEqual(value.NominalIntervalSeconds, RequestedIntervalSeconds) ||
            value.IntervalVarianceSecondsSquared < 0 || value.IntervalStandardDeviationSeconds < 0 ||
            !NearlyEqual(value.IntervalStandardDeviationSeconds, Math.Sqrt(value.IntervalVarianceSecondsSquared)) ||
            value.IntervalStandardDeviationSeconds > RequestedIntervalSeconds * MaximumIntervalStandardDeviationFraction ||
            value.MaximumAbsoluteIntervalDeviationSeconds > RequestedIntervalSeconds * MaximumIntervalDeviationFraction ||
            !value.PairTimestampsUnique || !value.PairSeparationWithinLimit || !value.PollAgeWithinLimit)
            return false;
        return value.PairingByPhase.Count == 2 && value.PairingByPhase.All(phase =>
            phase.PairCount >= MinimumPairsPerPhase && phase.MaximumAbsoluteSeparationSeconds <= MaximumPairSeparationSeconds &&
            phase.MaximumOraclePollAgeSeconds >= 0 &&
            phase.MaximumOraclePollAgeSeconds <= phase.MaximumOraclePollAgeLimitSeconds &&
            phase.MaximumOraclePollPeriodSeconds is > 0 and <= 10 &&
            NearlyEqual(phase.MaximumOraclePollAgeLimitSeconds,
                phase.MaximumOraclePollPeriodSeconds + MaximumPollAgeGraceSeconds));
    }

    private static string ResponseFailure(SensorResponseMetrics value) =>
        value.NativeConnectorCurrentRiseA < MinimumConnectorCurrentRiseA ||
        value.IndependentConnectorCurrentRiseA < MinimumConnectorCurrentRiseA
            ? $"connector current rise was {value.NativeConnectorCurrentRiseA:F2} A native and {value.IndependentConnectorCurrentRiseA:F2} A independent; each must be at least {MinimumConnectorCurrentRiseA:F1} A"
            : "idle/workload response or independent rail comparisons did not pass";

    private static string TimingFailure(SensorTimingMetrics value) =>
        $"host timing used {value.NativeSampleCount} samples over {value.NativeSampleSpanSeconds:F3} s; measured interval standard deviation {value.IntervalStandardDeviationSeconds * 1000:F1} ms and maximum deviation {value.MaximumAbsoluteIntervalDeviationSeconds * 1000:F1} ms";

    private static bool IsCommit(string value) => value.Length == 40 && value.All(Uri.IsHexDigit);
    private static string AdvanceMinor(string version)
    {
        var parsed = Version.Parse(version);
        return $"{parsed.Major}.{parsed.Minor + 1}.0";
    }
    private static string AdvanceMajor(string version) => $"{Version.Parse(version).Major + 1}.0.0";
    private static bool NearlyEqual(double first, double second) => Math.Abs(first - second) <= 1e-9;
    private static double Mean(IEnumerable<double> values)
    {
        var array = values.ToArray();
        return array.Length == 0 ? 0 : array.Average();
    }
    private static double SampleVariance(IEnumerable<double> values)
    {
        var array = values.ToArray();
        if (array.Length < 2) return 0;
        var mean = array.Average();
        return array.Sum(x => (x - mean) * (x - mean)) / (array.Length - 1);
    }
    private static double Percentile95(IEnumerable<double> values)
    {
        var sorted = values.Order().ToArray();
        return sorted.Length == 0 ? 0 : sorted[Math.Clamp((int)Math.Ceiling(sorted.Length * .95) - 1, 0, sorted.Length - 1)];
    }
}

internal sealed class SensorValidationEvidenceStore
{
    private const string FixedReleaseAssetBase = "https://github.com/Shaderx/ConnectorWatch/releases/download/driver-catalog/";
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMinutes(10);
    private readonly object _gate = new();
    private readonly string _dataDirectory;
    private readonly string? _bundledDirectory;
    private readonly HttpClient _httpClient;
    private readonly TimeSpan _requestTimeout;
    private readonly TimeProvider _timeProvider;
    private readonly Dictionary<string, Task> _inflight = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTimeOffset> _retryAfter = new(StringComparer.Ordinal);

    public SensorValidationEvidenceStore(string dataDirectory, string? bundledDirectory,
        HttpClient httpClient, TimeSpan requestTimeout, TimeProvider timeProvider)
    {
        _dataDirectory = Path.GetFullPath(dataDirectory);
        _bundledDirectory = string.IsNullOrWhiteSpace(bundledDirectory)
            ? null : Path.GetFullPath(bundledDirectory);
        _httpClient = httpClient;
        _requestTimeout = requestTimeout;
        _timeProvider = timeProvider;
    }

    public SensorValidationSnapshot Resolve(DriverIdentity identity, DriverCatalogEntry entry, string profile,
        string readerVersion, string appVersion, long catalogRevision, CancellationToken cancellationToken)
    {
        if (entry.Decision != "approved" || !SensorValidationPolicy.IsDigest(entry.EvidenceSha256) ||
            !entry.MatchesIdentity(identity, profile) || !entry.SupportsReader(readerVersion) ||
            !entry.SupportsApp(appVersion))
            return SensorValidationSnapshot.NotRun(entry.EvidenceSha256, catalogRevision,
                "Sensor validation requires the current exact approved driver entry.");

        var localPath = Path.Combine(_dataDirectory, "driver-validation", "evidence-" + entry.EvidenceSha256 + ".json");
        var bundledPath = _bundledDirectory is null ? null :
            Path.Combine(_bundledDirectory, "evidence-" + entry.EvidenceSha256 + ".json");
        SensorValidationSnapshot? rejected = null;
        foreach (var path in new[] { bundledPath, localPath })
        {
            if (path is null || !File.Exists(path)) continue;
            try
            {
                var length = new FileInfo(path).Length;
                if (length is < 1 or > SensorValidationPolicy.MaximumEvidenceBytes)
                {
                    rejected ??= SensorValidationSnapshot.Rejected(entry.EvidenceSha256, catalogRevision,
                        "The local validation evidence file is empty or oversized.");
                    continue;
                }
                var snapshot = SensorValidationPolicy.SnapshotFromEvidence(File.ReadAllBytes(path),
                    entry.EvidenceSha256, entry, profile, readerVersion, appVersion, catalogRevision);
                if (snapshot.EvidenceState is "VERIFIED" or "LEGACY") return snapshot;
                rejected ??= snapshot;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                rejected ??= SensorValidationSnapshot.Rejected(entry.EvidenceSha256, catalogRevision,
                    "The local validation evidence file could not be read: " + ex.Message);
            }
        }
        RequestDownload(entry, profile, readerVersion, appVersion, catalogRevision, cancellationToken);
        return rejected ?? SensorValidationSnapshot.Missing(entry.EvidenceSha256, catalogRevision);
    }

    private void RequestDownload(DriverCatalogEntry entry, string profile, string readerVersion,
        string appVersion, long catalogRevision, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (_inflight.ContainsKey(entry.EvidenceSha256) ||
                _retryAfter.TryGetValue(entry.EvidenceSha256, out var retryAfter) && _timeProvider.GetUtcNow() < retryAfter)
                return;
            var task = DownloadAsync(entry, profile, readerVersion, appVersion, catalogRevision, cancellationToken);
            _inflight[entry.EvidenceSha256] = task;
            _ = task.ContinueWith(_ =>
            {
                lock (_gate)
                {
                    _inflight.Remove(entry.EvidenceSha256);
                    _retryAfter[entry.EvidenceSha256] = _timeProvider.GetUtcNow() + RetryDelay;
                }
            }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
    }

    private async Task DownloadAsync(DriverCatalogEntry entry, string profile,
        string readerVersion, string appVersion, long catalogRevision, CancellationToken cancellationToken)
    {
        try
        {
            var endpoint = new Uri(new Uri(FixedReleaseAssetBase), "evidence-" + entry.EvidenceSha256 + ".json");
            using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(_requestTimeout);
            using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead,
                timeout.Token).ConfigureAwait(false);
            var finalUri = response.RequestMessage?.RequestUri;
            if (finalUri is null || finalUri.Scheme != Uri.UriSchemeHttps ||
                !(finalUri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) ||
                  finalUri.Host.EndsWith(".githubusercontent.com", StringComparison.OrdinalIgnoreCase)))
                throw new InvalidDataException("Validation evidence retrieval redirected outside the fixed HTTPS release assets.");
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength is long length &&
                length > SensorValidationPolicy.MaximumEvidenceBytes)
                throw new InvalidDataException("Downloaded validation evidence exceeds its size limit.");
            var bytes = await ReadBoundedAsync(response.Content, timeout.Token).ConfigureAwait(false);
            var snapshot = SensorValidationPolicy.SnapshotFromEvidence(bytes, entry.EvidenceSha256,
                entry, profile, readerVersion, appVersion, catalogRevision);
            if (snapshot.EvidenceState is not ("VERIFIED" or "LEGACY"))
                throw new InvalidDataException(snapshot.Detail);
            var directory = Path.Combine(_dataDirectory, "driver-validation");
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, "evidence-" + entry.EvidenceSha256 + ".json");
            var temporaryPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                await File.WriteAllBytesAsync(temporaryPath, bytes, timeout.Token).ConfigureAwait(false);
                File.Move(temporaryPath, path, overwrite: true);
            }
            finally
            {
                try { if (File.Exists(temporaryPath)) File.Delete(temporaryPath); }
                catch (IOException) { }
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException or
                                   InvalidDataException or UnauthorizedAccessException)
        {
            // The current decision remains usable; the next lookup reports the precise evidence state.
        }
    }

    private static async Task<byte[]> ReadBoundedAsync(HttpContent content, CancellationToken cancellationToken)
    {
        await using var stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var output = new MemoryStream();
        var buffer = new byte[8192];
        while (true)
        {
            var read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            if (output.Length + read > SensorValidationPolicy.MaximumEvidenceBytes)
                throw new InvalidDataException("Downloaded validation evidence exceeds its size limit.");
            output.Write(buffer, 0, read);
        }
        if (output.Length == 0) throw new InvalidDataException("Downloaded validation evidence is empty.");
        return output.ToArray();
    }
}

public sealed class MaintainerOraclePairingProvenance
{
    public string SourceName { get; set; } = "";
    public string SensorId { get; set; } = "";
    public string PairingMethod { get; set; } = "";
    public string VoltageUnit { get; set; } = "";
    public string PowerUnit { get; set; } = "";
    public string CurrentDerivation { get; set; } = "";
}
