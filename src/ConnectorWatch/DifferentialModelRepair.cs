using System.Globalization;
using System.IO.Compression;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ConnectorWatch;

public sealed record DifferentialModelRepairInputHash(
    [property: JsonPropertyName("file")] string File,
    [property: JsonPropertyName("sha256")] string Sha256);

public sealed record DifferentialModelRepairBinCheck(
    [property: JsonPropertyName("bin_watts")] int BinWatts,
    [property: JsonPropertyName("accepted_reference_v")] double AcceptedReferenceVolts,
    [property: JsonPropertyName("predicted_center_v")] double? PredictedCenterVolts,
    [property: JsonPropertyName("difference_v")] double? DifferenceVolts,
    [property: JsonPropertyName("sample_count")] int SampleCount,
    [property: JsonPropertyName("available")] bool Available,
    [property: JsonPropertyName("detail")] string Detail);

public sealed record DifferentialModelRepairBandReport(
    [property: JsonPropertyName("bin_watts")] int BinWatts,
    [property: JsonPropertyName("accepted_reference_v")] double AcceptedReferenceVolts,
    [property: JsonPropertyName("training_qualified_samples")] int TrainingQualifiedSamples,
    [property: JsonPropertyName("training_fit_samples")] int TrainingFitSamples,
    [property: JsonPropertyName("fit_envelope_covers_band")] bool FitEnvelopeCoversBand,
    [property: JsonPropertyName("fit_expected_center_v")] double? FitExpectedCenterVolts,
    [property: JsonPropertyName("fit_expected_center_delta_v")] double? FitExpectedCenterDeltaVolts,
    [property: JsonPropertyName("training_residual_median_v")] double? TrainingResidualMedianVolts,
    [property: JsonPropertyName("training_voltage_delta_median_v")] double? TrainingVoltageDeltaMedianVolts,
    [property: JsonPropertyName("holdout_input_samples")] int HoldoutInputSamples,
    [property: JsonPropertyName("holdout_prediction_samples")] int HoldoutPredictionSamples,
    [property: JsonPropertyName("holdout_residual_median_v")] double? HoldoutResidualMedianVolts,
    [property: JsonPropertyName("holdout_voltage_delta_median_v")] double? HoldoutVoltageDeltaMedianVolts);

public sealed record DifferentialModelRepairHoldoutReport(
    [property: JsonPropertyName("start_utc")] DateTimeOffset StartUtc,
    [property: JsonPropertyName("end_utc")] DateTimeOffset EndUtc,
    [property: JsonPropertyName("input_rows")] int InputRows,
    [property: JsonPropertyName("prediction_count")] int PredictionCount,
    [property: JsonPropertyName("unavailable_count")] int UnavailableCount,
    [property: JsonPropertyName("filtered_identity_count")] int FilteredIdentityCount,
    [property: JsonPropertyName("residual_p05_v")] double? ResidualP05Volts,
    [property: JsonPropertyName("residual_median_v")] double? ResidualMedianVolts,
    [property: JsonPropertyName("residual_p95_v")] double? ResidualP95Volts,
    [property: JsonPropertyName("unavailable_reasons")] IReadOnlyDictionary<string, int> UnavailableReasons);

public sealed record DifferentialModelRepairReport(
    [property: JsonPropertyName("accepted_at_utc")] DateTimeOffset AcceptedAtUtc,
    [property: JsonPropertyName("replay_start_utc")] DateTimeOffset ReplayStartUtc,
    [property: JsonPropertyName("reconstruction_scope")] string ReconstructionScope,
    [property: JsonPropertyName("accepted_boundary_status")] string AcceptedBoundaryStatus,
    [property: JsonPropertyName("first_logged_unverified_utc")] DateTimeOffset FirstLoggedUnverifiedUtc,
    [property: JsonPropertyName("first_qualified_utc")] DateTimeOffset FirstQualifiedUtc,
    [property: JsonPropertyName("accepted_boundary_first_qualified_utc")] DateTimeOffset? AcceptedBoundaryFirstQualifiedUtc,
    [property: JsonPropertyName("training_end_utc")] DateTimeOffset TrainingEndUtc,
    [property: JsonPropertyName("input_row_count")] int InputRowCount,
    [property: JsonPropertyName("qualified_sample_count")] int QualifiedSampleCount,
    [property: JsonPropertyName("training_sample_limit")] int TrainingSampleLimit,
    [property: JsonPropertyName("accepted_boundary_matches_qualification")] bool AcceptedBoundaryMatchesQualification,
    [property: JsonPropertyName("replayed_analysis_status_mismatches")] int ReplayedAnalysisStatusMismatches,
    [property: JsonPropertyName("identity_filtered_rows")] int IdentityFilteredRows,
    [property: JsonPropertyName("settlement_resets")] int SettlementResets,
    [property: JsonPropertyName("fit_state")] string FitState,
    [property: JsonPropertyName("fit_input_samples")] int FitInputSamples,
    [property: JsonPropertyName("fit_qualified_samples")] int FitQualifiedSamples,
    [property: JsonPropertyName("fit_rejected_samples")] int FitRejectedSamples,
    [property: JsonPropertyName("effective_rank")] int EffectiveRank,
    [property: JsonPropertyName("condition_number")] double? ConditionNumber,
    [property: JsonPropertyName("load_span_w")] double? LoadSpanWatts,
    [property: JsonPropertyName("rmse_v")] double? RootMeanSquareErrorVolts,
    [property: JsonPropertyName("median_absolute_error_v")] double? MedianAbsoluteErrorVolts,
    [property: JsonPropertyName("artifact_hash")] string ArtifactHash,
    [property: JsonPropertyName("accepted_bin_checks")] IReadOnlyList<DifferentialModelRepairBinCheck> AcceptedBinChecks,
    [property: JsonPropertyName("bands")] IReadOnlyList<DifferentialModelRepairBandReport> Bands,
    [property: JsonPropertyName("heldout")] DifferentialModelRepairHoldoutReport Heldout);

public sealed record DifferentialModelRepairProvenance(
    [property: JsonPropertyName("accepted_at_utc")] DateTimeOffset AcceptedAtUtc,
    [property: JsonPropertyName("accepted_reference_fingerprint")] string AcceptedReferenceFingerprint,
    [property: JsonPropertyName("accepted_reference_identity")] string AcceptedReferenceIdentity,
    [property: JsonPropertyName("differential_identity_key")] string DifferentialIdentityKey,
    [property: JsonPropertyName("model_options_fingerprint")] string ModelOptionsFingerprint,
    [property: JsonPropertyName("previous_artifact_sha256")] string? PreviousArtifactSha256,
    [property: JsonPropertyName("previous_artifact_hash")] string? PreviousArtifactHash,
    [property: JsonPropertyName("candidate_artifact_hash")] string CandidateArtifactHash,
    [property: JsonPropertyName("training_start_utc")] DateTimeOffset TrainingStartUtc,
    [property: JsonPropertyName("training_end_utc")] DateTimeOffset TrainingEndUtc,
    [property: JsonPropertyName("input_hashes")] IReadOnlyList<DifferentialModelRepairInputHash> InputHashes);

public sealed record DifferentialModelRepairPackage(
    [property: JsonPropertyName("schema_version")] int SchemaVersion,
    [property: JsonPropertyName("artifact_json")] string ArtifactJson,
    [property: JsonPropertyName("provenance")] DifferentialModelRepairProvenance Provenance,
    [property: JsonPropertyName("report")] DifferentialModelRepairReport Report,
    [property: JsonPropertyName("package_sha256")] string PackageSha256);

public sealed record DifferentialModelRepairReceipt(
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("prepared_at_utc")] DateTimeOffset PreparedAtUtc,
    [property: JsonPropertyName("imported_at_utc")] DateTimeOffset? ImportedAtUtc,
    [property: JsonPropertyName("candidate_package_sha256")] string CandidatePackageSha256,
    [property: JsonPropertyName("previous_artifact_sha256")] string? PreviousArtifactSha256,
    [property: JsonPropertyName("previous_artifact_hash")] string? PreviousArtifactHash,
    [property: JsonPropertyName("new_artifact_hash")] string NewArtifactHash,
    [property: JsonPropertyName("accepted_reference_fingerprint")] string AcceptedReferenceFingerprint,
    [property: JsonPropertyName("model_options_fingerprint")] string ModelOptionsFingerprint,
    [property: JsonPropertyName("input_hashes")] IReadOnlyList<DifferentialModelRepairInputHash> InputHashes,
    [property: JsonPropertyName("training_start_utc")] DateTimeOffset TrainingStartUtc,
    [property: JsonPropertyName("training_end_utc")] DateTimeOffset TrainingEndUtc,
    [property: JsonPropertyName("holdout")] DifferentialModelRepairHoldoutReport Holdout);

/// <summary>Prepare and import a fitted artifact from a bounded historical replay.</summary>
public static class DifferentialModelRepairCommand
{
    public const int CurrentPackageSchemaVersion = 1;
    public const int MaximumTrainingSamples = 10_000;
    const string CandidateFileName = "differential-model-repair-candidate.json";
    const string ReportFileName = "differential-model-repair-report.json";
    const string ReceiptFileName = "differential-model-repair-receipt.json";

    static readonly JsonSerializerOptions JsonIndented = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };
    static readonly JsonSerializerOptions JsonCompact = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false,
        Converters = { new JsonStringEnumConverter() },
    };

    public static bool TryHandle(string[] args, out int exitCode)
    {
        exitCode = 0;
        string? prepare = Option(args, "--prepare-differential-model-repair");
        string? import = Option(args, "--import-differential-model-repair");
        if (prepare is null && import is null) return false;
        try
        {
            if (prepare is not null)
            {
                string root = Path.GetFullPath(prepare);
                var package = PrepareCapture(root);
                string candidatePath = ResolveCaptureOutput(root,
                    Option(args, "--output"), CandidateFileName);
                string reportPath = ResolveCaptureOutput(root,
                    Option(args, "--report"), ReportFileName);
                string receiptPath = ResolveCaptureOutput(root, null, ReceiptFileName);
                WritePrepared(package, candidatePath, reportPath, receiptPath);
                Console.WriteLine(JsonSerializer.Serialize(new
                {
                    succeeded = true,
                    candidate = candidatePath,
                    report = reportPath,
                    receipt = receiptPath,
                    artifact_hash = package.Provenance.CandidateArtifactHash,
                    fit_state = package.Report.FitState,
                    qualified_samples = package.Report.QualifiedSampleCount,
                    fit_samples = package.Report.FitQualifiedSamples,
                    training_start_utc = package.Report.FirstQualifiedUtc,
                    training_end_utc = package.Report.TrainingEndUtc,
                    heldout_predictions = package.Report.Heldout.PredictionCount,
                }, JsonIndented));
                return true;
            }

            string configPath = DeploymentPaths.ResolveConfigPath(Option(args, "--config"));
            var config = JsonSerializer.Deserialize<Config>(File.ReadAllText(configPath))
                ?? throw new InvalidDataException("Configuration is empty.");
            config.Validate();
            string dataDirectory = DeploymentPaths.ResolveDataDirectory(configPath,
                config.DataDirectory);
            string candidate = Path.GetFullPath(import!);
            string clientId = "connectorwatch-repair-" + Guid.NewGuid().ToString("N");
            var hello = SendControlRequest(dataDirectory, new ControlRequest("hello", clientId));
            if (hello is null || !hello.Ok)
                throw new IOException("No compatible ConnectorWatch daemon answered the control endpoint.");
            var response = SendControlRequest(dataDirectory, new ControlRequest(
                "import-differential-model-repair", clientId, hello.InstanceId,
                Note: candidate));
            if (response is null)
                throw new IOException("The daemon did not return a complete import response.");
            Console.WriteLine(JsonSerializer.Serialize(response, JsonIndented));
            exitCode = response.Ok ? 0 : 1;
            return true;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or
            ArgumentException or FormatException or NotSupportedException or
            JsonException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine("Differential model repair failed: " + ex.Message);
            exitCode = 1;
            return true;
        }
    }

    internal static DifferentialModelRepairPackage PrepareCapture(string captureDirectory)
    {
        string root = Path.GetFullPath(captureDirectory);
        if (!Directory.Exists(root)) throw new DirectoryNotFoundException(root);
        string configPath = Path.Combine(root, "config.json");
        string referencePath = Path.Combine(root, "reference.json");
        string modelPath = Path.Combine(root, "differential-model.json");
        foreach (var required in new[] { configPath, referencePath, modelPath })
            if (!File.Exists(required)) throw new FileNotFoundException("Required repair input is missing.", required);

        string configJson = File.ReadAllText(configPath);
        var config = JsonSerializer.Deserialize<Config>(configJson)
            ?? throw new InvalidDataException("Captured config.json is empty.");
        config.Validate();
        string referenceJson = File.ReadAllText(referencePath);
        var reference = ReferencePersistence.Deserialize(referenceJson);
        var accepted = reference.Accepted
            ?? throw new InvalidDataException("Captured reference has no accepted snapshot.");
        ValidateConfigBinding(config, accepted.Identity);

        string previousArtifactJson = File.ReadAllText(modelPath);
        var previousArtifact = DifferentialModelPersistence.Deserialize(previousArtifactJson);
        if (previousArtifact.IsFitted || previousArtifact.Diagnostics.State != DifferentialModelState.INSUFFICIENT_DATA)
            throw new InvalidDataException("Repair can replace only the hash-valid INSUFFICIENT_DATA artifact.");
        var expectedOptions = Program.BuildDifferentialOptions(config, accepted.Identity) with
        {
            ArtifactCreatedAtUtc = accepted.AcceptedAtUtc,
        };
        var expectedIdentity = BuildDifferentialIdentity(accepted.Identity);
        if (!previousArtifact.Identity.Matches(expectedIdentity) ||
            previousArtifact.LoadProxy != expectedOptions.LoadProxy ||
            !string.Equals(OptionsFingerprint(previousArtifact.Settings),
                OptionsFingerprint(expectedOptions), StringComparison.Ordinal))
            throw new InvalidDataException("Captured artifact identity or settings do not match the accepted reference and config.");

        DateTimeOffset acceptedAt = accepted.AcceptedAtUtc.ToUniversalTime();
        DateTimeOffset heldoutEnd = acceptedAt.AddHours(24);
        var telemetryPaths = FindTelemetryFiles(root, heldoutEnd).ToArray();
        if (telemetryPaths.Length == 0)
            throw new InvalidDataException("No telemetry files cover the retained pre-acceptance history and 24-hour holdout.");
        var rows = telemetryPaths.SelectMany(ReadTelemetry)
            .Where(row => row.Timestamp <= heldoutEnd)
            .OrderBy(row => row.Timestamp).ToArray();
        var trainingRows = rows.Where(row => row.Timestamp < acceptedAt).ToArray();
        var boundaryStart = new DateTimeOffset(acceptedAt.UtcDateTime.Date, TimeSpan.Zero);
        var boundary = trainingRows.FirstOrDefault(row => row.Timestamp >= boundaryStart &&
            IsAcceptedSourceRow(row, accepted.Identity, config) &&
            string.Equals(row.Status, "REFERENCE_UNVERIFIED", StringComparison.Ordinal))
            ?? throw new InvalidDataException("The accepted-date telemetry has no REFERENCE_UNVERIFIED boundary for compatible measurement semantics.");
        var compatibleTrainingRows = trainingRows.Where(row =>
            IsAcceptedSourceRow(row, accepted.Identity, config)).ToArray();
        if (compatibleTrainingRows.Length == 0)
            throw new InvalidDataException("No retained pre-acceptance telemetry matches the accepted GPU, rail, load source, and qualification semantics.");
        DateTimeOffset replayStart = compatibleTrainingRows[0].Timestamp;

        var analysis = new Analysis(config);
        var qualifiedSamples = new List<DifferentialSample>();
        DateTimeOffset? firstQualified = null;
        DateTimeOffset? boundaryFirstQualified = null;
        DateTimeOffset? previousSensorTimestamp = null;
        DateTimeOffset? previousRowTimestamp = null;
        long? previousPollStart = null;
        double elapsedClock = 0;
        int replayInputRows = 0;
        int statusMismatches = 0;
        int identityFiltered = 0;
        int settlementResets = 0;
        double gapResetSeconds = Math.Max(config.SampleSeconds * 3, config.MaxAgeSeconds * 2);
        foreach (var row in trainingRows.Where(row => row.Timestamp >= replayStart))
        {
            replayInputRows++;
            double rowGapSeconds = previousRowTimestamp.HasValue
                ? (row.Timestamp - previousRowTimestamp.Value).TotalSeconds : 0;
            if (previousRowTimestamp.HasValue)
            {
                elapsedClock += Math.Max(0, rowGapSeconds);
                if (rowGapSeconds > gapResetSeconds ||
                    previousPollStart is long previousStart && row.PollStartMonotonic is long currentStart &&
                    currentStart <= previousStart)
                {
                    analysis.Gap();
                    previousSensorTimestamp = null;
                    settlementResets++;
                }
            }
            previousRowTimestamp = row.Timestamp;
            previousPollStart = row.PollStartMonotonic;

            if (!IsAcceptedSourceRow(row, accepted.Identity, config))
            {
                analysis.Gap();
                previousSensorTimestamp = null;
                identityFiltered++;
                continue;
            }
            if (row.Voltage is not double voltage || row.VoltageTimestamp is not DateTimeOffset voltageTimestamp ||
                row.AnalysisPower is not double analysisPower)
            {
                analysis.Gap();
                continue;
            }
            if (previousSensorTimestamp.HasValue && voltageTimestamp <= previousSensorTimestamp.Value)
            {
                analysis.Gap();
                previousSensorTimestamp = null;
                settlementResets++;
                continue;
            }
            previousSensorTimestamp = voltageTimestamp;
            bool fresh = IsFresh(row, config.MaxAgeSeconds);
            var result = analysis.Add(voltageTimestamp, analysisPower, voltage,
                elapsedClock, fresh);
            if (row.Timestamp >= boundary.Timestamp &&
                !string.Equals(result.Status, row.Status, StringComparison.Ordinal))
                statusMismatches++;
            if (result.LoadQualification?.IsQualified != true)
                continue;
            firstQualified ??= row.Timestamp;
            if (row.Timestamp >= boundary.Timestamp && row.Timestamp < acceptedAt &&
                row.Timestamp.UtcDateTime.Date == acceptedAt.UtcDateTime.Date)
                boundaryFirstQualified ??= row.Timestamp;
            qualifiedSamples.Add(ToDifferentialSample(row, accepted.Identity, config,
                result.LoadQualification.IsQualified, fresh));
        }

        if (firstQualified is null || qualifiedSamples.Count == 0)
            throw new InvalidDataException("The retained pre-acceptance window contains no qualified differential samples.");
        if (boundaryFirstQualified != boundary.Timestamp)
            throw new InvalidDataException("The accepted-date replay did not reproduce the first qualified REFERENCE_UNVERIFIED row.");
        var fitSamples = SelectTrainingSamples(qualifiedSamples,
            expectedOptions.LoadProxy, config.BinWatts, MaximumTrainingSamples);

        var fit = DifferentialModelTrainer.Fit(fitSamples, expectedOptions);
        if (!fit.IsUsable || fit.Artifact.Diagnostics.State != DifferentialModelState.FITTED)
            throw new InvalidDataException("Historical replay did not produce a FITTED model: " + fit.Detail);

        var acceptedBinChecks = CompareAcceptedBins(fit.Artifact, accepted, fitSamples,
            config.BinWatts);
        var holdoutEvaluation = EvaluateHoldout(fit.Artifact, accepted, config, rows,
            acceptedAt, heldoutEnd);
        var bandReports = BuildBandReports(fit.Artifact, accepted, qualifiedSamples,
            fitSamples, holdoutEvaluation.Bands, config.BinWatts);
        string previousArtifactHash = Sha256(Encoding.UTF8.GetBytes(previousArtifactJson));
        var inputHashes = new List<DifferentialModelRepairInputHash>
        {
            new("config.json", Sha256(Encoding.UTF8.GetBytes(configJson))),
            new("reference.json", Sha256(Encoding.UTF8.GetBytes(referenceJson))),
            new("differential-model.json", previousArtifactHash),
        };
        inputHashes.AddRange(telemetryPaths.Select(path => new DifferentialModelRepairInputHash(
            Path.GetFileName(path), Sha256File(path))));

        var diagnostics = fit.Artifact.Diagnostics;
        var report = new DifferentialModelRepairReport(acceptedAt,
            replayStart, "compatible_preacceptance_telemetry_reconstruction",
            "REFERENCE_UNVERIFIED", boundary.Timestamp, firstQualified.Value,
            boundaryFirstQualified,
            fit.Artifact.TrainingEndUtc, replayInputRows,
            qualifiedSamples.Count, MaximumTrainingSamples,
            boundaryFirstQualified == boundary.Timestamp, statusMismatches,
            identityFiltered, settlementResets,
            diagnostics.State.ToString(), diagnostics.InputSamples,
            diagnostics.QualifiedSamples, diagnostics.RejectedSamples,
            diagnostics.EffectiveRank, diagnostics.ConditionNumber,
            diagnostics.LoadSpan, diagnostics.RootMeanSquareErrorVolts,
            diagnostics.MedianAbsoluteErrorVolts, fit.Artifact.ArtifactHash,
            acceptedBinChecks, bandReports, holdoutEvaluation.Report);
        var provenance = new DifferentialModelRepairProvenance(acceptedAt,
            AcceptedReferenceFingerprint(accepted), accepted.Identity.CanonicalJson,
            fit.Artifact.Identity.CanonicalKey, OptionsFingerprint(expectedOptions),
            previousArtifactHash, previousArtifact.ArtifactHash,
            fit.Artifact.ArtifactHash, fit.Artifact.TrainingStartUtc,
            fit.Artifact.TrainingEndUtc, inputHashes);
        string artifactJson = DifferentialModelPersistence.Serialize(fit.Artifact);
        var unhashed = new DifferentialModelRepairPackage(CurrentPackageSchemaVersion,
            artifactJson, provenance, report, string.Empty);
        return unhashed with { PackageSha256 = ComputePackageHash(unhashed) };
    }

    internal static DifferentialModelArtifact ImportPackage(string packagePath,
        string activeModelPath, string receiptPath, AcceptedReferenceModel? currentAccepted,
        DifferentialModelArtifact? runtimeArtifact, DifferentialModelOptions currentOptions,
        DateTimeOffset importedAtUtc)
    {
        if (string.IsNullOrWhiteSpace(packagePath))
            throw new InvalidDataException("The repair package path is required.");
        var package = DeserializePackage(File.ReadAllText(Path.GetFullPath(packagePath)));
        var candidateArtifact = DifferentialModelPersistence.Deserialize(package.ArtifactJson);
        if (currentAccepted is null)
            throw new InvalidDataException("An accepted reference is required before model repair import.");
        if (candidateArtifact.Diagnostics.State != DifferentialModelState.FITTED ||
            !candidateArtifact.IsFitted)
            throw new InvalidDataException("Only a successfully fitted repair artifact can be imported.");
        var provenance = package.Provenance;
        if (provenance.AcceptedAtUtc != currentAccepted.AcceptedAtUtc ||
            !string.Equals(provenance.AcceptedReferenceFingerprint,
                AcceptedReferenceFingerprint(currentAccepted), StringComparison.Ordinal) ||
            !string.Equals(provenance.AcceptedReferenceIdentity,
                currentAccepted.Identity.CanonicalJson, StringComparison.Ordinal))
            throw new InvalidDataException("The accepted reference changed after this candidate was prepared.");

        var expectedOptions = currentOptions with
        {
            ArtifactCreatedAtUtc = currentAccepted.AcceptedAtUtc,
        };
        if (!string.Equals(provenance.ModelOptionsFingerprint,
                OptionsFingerprint(expectedOptions), StringComparison.Ordinal) ||
            !candidateArtifact.Identity.Matches(expectedOptions.Identity) ||
            !string.Equals(candidateArtifact.Identity.CanonicalKey,
                provenance.DifferentialIdentityKey, StringComparison.Ordinal) ||
            !string.Equals(OptionsFingerprint(candidateArtifact.Settings),
                provenance.ModelOptionsFingerprint, StringComparison.Ordinal))
            throw new InvalidDataException("The repair candidate identity or settings do not match the active monitor.");

        string modelPath = Path.GetFullPath(activeModelPath);
        if (!File.Exists(modelPath) || runtimeArtifact is null)
            throw new InvalidDataException("An existing matching INSUFFICIENT_DATA artifact is required for repair import.");
        string? activeJson = File.Exists(modelPath) ? File.ReadAllText(modelPath) : null;
        string? activeRawHash = activeJson is null ? null : Sha256(Encoding.UTF8.GetBytes(activeJson));
        if (!string.Equals(activeRawHash, provenance.PreviousArtifactSha256,
                StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The active differential artifact changed after candidate preparation.");
        DifferentialModelArtifact? diskArtifact = activeJson is null
            ? null : DifferentialModelPersistence.Deserialize(activeJson);
        if (diskArtifact?.IsFitted == true || runtimeArtifact?.IsFitted == true)
            throw new InvalidDataException("A fitted active differential artifact cannot be replaced.");
        if (diskArtifact?.ArtifactHash != provenance.PreviousArtifactHash ||
            runtimeArtifact?.ArtifactHash != diskArtifact?.ArtifactHash)
            throw new InvalidDataException("The active artifact hash does not match the repair candidate.");
        if (diskArtifact is not null &&
            (diskArtifact.Diagnostics.State != DifferentialModelState.INSUFFICIENT_DATA ||
             !diskArtifact.Identity.Matches(expectedOptions.Identity) ||
             !string.Equals(OptionsFingerprint(diskArtifact.Settings),
                 provenance.ModelOptionsFingerprint, StringComparison.Ordinal)))
            throw new InvalidDataException("Only the matching INSUFFICIENT_DATA artifact can be repaired.");
        if (!string.Equals(provenance.CandidateArtifactHash,
                candidateArtifact.ArtifactHash, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Candidate artifact hash does not match its provenance.");

        var receipt = new DifferentialModelRepairReceipt("IMPORT_PENDING",
            importedAtUtc.ToUniversalTime(), null, package.PackageSha256,
            provenance.PreviousArtifactSha256, provenance.PreviousArtifactHash,
            candidateArtifact.ArtifactHash, provenance.AcceptedReferenceFingerprint,
            provenance.ModelOptionsFingerprint, provenance.InputHashes,
            provenance.TrainingStartUtc, provenance.TrainingEndUtc,
            package.Report.Heldout);
        string receiptFullPath = Path.GetFullPath(receiptPath);
        Directory.CreateDirectory(Path.GetDirectoryName(receiptFullPath)!);
        HybridStorage.Atomic(receiptFullPath, JsonSerializer.Serialize(receipt, JsonIndented));
        HybridStorage.Atomic(modelPath, DifferentialModelPersistence.Serialize(candidateArtifact));
        receipt = receipt with { Status = "IMPORTED", ImportedAtUtc = importedAtUtc.ToUniversalTime() };
        HybridStorage.Atomic(receiptFullPath, JsonSerializer.Serialize(receipt, JsonIndented));
        return candidateArtifact;
    }

    internal static DifferentialModelRepairPackage CreatePackage(
        DifferentialModelFitResult fit, DifferentialModelArtifact previousArtifact,
        AcceptedReferenceModel accepted, DifferentialModelOptions options,
        DifferentialModelRepairReport report,
        IReadOnlyList<DifferentialModelRepairInputHash>? inputHashes = null)
    {
        if (!fit.IsUsable || fit.Artifact.Diagnostics.State != DifferentialModelState.FITTED)
            throw new InvalidDataException("A repair package requires a FITTED result.");
        var provenance = new DifferentialModelRepairProvenance(
            accepted.AcceptedAtUtc,
            AcceptedReferenceFingerprint(accepted),
            accepted.Identity.CanonicalJson,
            fit.Artifact.Identity.CanonicalKey,
            OptionsFingerprint(options with { ArtifactCreatedAtUtc = accepted.AcceptedAtUtc }),
            Sha256(Encoding.UTF8.GetBytes(DifferentialModelPersistence.Serialize(previousArtifact))),
            previousArtifact.ArtifactHash,
            fit.Artifact.ArtifactHash,
            fit.Artifact.TrainingStartUtc,
            fit.Artifact.TrainingEndUtc,
            inputHashes ?? Array.Empty<DifferentialModelRepairInputHash>());
        var package = new DifferentialModelRepairPackage(CurrentPackageSchemaVersion,
            DifferentialModelPersistence.Serialize(fit.Artifact), provenance, report,
            string.Empty);
        return package with { PackageSha256 = ComputePackageHash(package) };
    }

    internal static DifferentialModelRepairPackage DeserializePackage(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) throw new InvalidDataException("Repair package is empty.");
        var package = JsonSerializer.Deserialize<DifferentialModelRepairPackage>(json, JsonIndented)
            ?? throw new InvalidDataException("Repair package is empty.");
        if (package.Provenance is null || package.Report is null)
            throw new InvalidDataException("Repair package provenance and report are required.");
        if (package.SchemaVersion != CurrentPackageSchemaVersion ||
            !string.Equals(package.PackageSha256, ComputePackageHash(package),
                StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Repair package schema or integrity check failed.");
        var artifact = DifferentialModelPersistence.Deserialize(package.ArtifactJson);
        if (!artifact.IsFitted || artifact.Diagnostics.State != DifferentialModelState.FITTED ||
            !string.Equals(artifact.ArtifactHash, package.Provenance.CandidateArtifactHash,
                StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(OptionsFingerprint(artifact.Settings),
                package.Provenance.ModelOptionsFingerprint, StringComparison.Ordinal) ||
            !string.Equals(artifact.Identity.CanonicalKey,
                package.Provenance.DifferentialIdentityKey, StringComparison.Ordinal))
            throw new InvalidDataException("Repair package does not contain its recorded fitted artifact.");
        if (package.Provenance.AcceptedAtUtc == default ||
            artifact.TrainingEndUtc >= package.Provenance.AcceptedAtUtc ||
            artifact.CreatedAtUtc != package.Provenance.AcceptedAtUtc)
            throw new InvalidDataException("Repair training must precede reference acceptance and retain its freeze timestamp.");
        if (package.Provenance.TrainingStartUtc != artifact.TrainingStartUtc ||
            package.Provenance.TrainingEndUtc != artifact.TrainingEndUtc ||
            package.Report.TrainingEndUtc != artifact.TrainingEndUtc ||
            package.Report.AcceptedAtUtc != package.Provenance.AcceptedAtUtc)
            throw new InvalidDataException("Repair training timestamps do not match the artifact and accepted-reference provenance.");
        return package;
    }

    internal static string AcceptedReferenceFingerprint(AcceptedReferenceModel accepted)
    {
        var bins = string.Join("\n", accepted.Bins.OrderBy(pair => pair.Key).Select(pair =>
            string.Join("|", pair.Key.ToString(CultureInfo.InvariantCulture),
                pair.Value.ReferenceVolts?.ToString("R", CultureInfo.InvariantCulture) ?? "null",
                pair.Value.ReferenceP05Volts?.ToString("R", CultureInfo.InvariantCulture) ?? "null",
                pair.Value.LearningSamples.ToString(CultureInfo.InvariantCulture),
                pair.Value.ObservedSamples.ToString(CultureInfo.InvariantCulture))));
        string value = accepted.AcceptedAtUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture) +
            "\n" + accepted.Identity.CanonicalJson + "\n" + bins;
        return Sha256(Encoding.UTF8.GetBytes(value));
    }

    internal static string OptionsFingerprint(DifferentialModelOptions options) =>
        Sha256(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(options, JsonCompact)));

    internal static void WritePrepared(DifferentialModelRepairPackage package,
        string candidatePath, string reportPath, string receiptPath)
    {
        _ = DeserializePackage(JsonSerializer.Serialize(package, JsonIndented));
        var receipt = new DifferentialModelRepairReceipt("PREPARED",
            DateTimeOffset.UtcNow, null, package.PackageSha256,
            package.Provenance.PreviousArtifactSha256,
            package.Provenance.PreviousArtifactHash,
            package.Provenance.CandidateArtifactHash,
            package.Provenance.AcceptedReferenceFingerprint,
            package.Provenance.ModelOptionsFingerprint,
            package.Provenance.InputHashes,
            package.Provenance.TrainingStartUtc,
            package.Provenance.TrainingEndUtc,
            package.Report.Heldout);
        HybridStorage.Atomic(Path.GetFullPath(candidatePath),
            JsonSerializer.Serialize(package, JsonIndented));
        HybridStorage.Atomic(Path.GetFullPath(reportPath),
            JsonSerializer.Serialize(package.Report, JsonIndented));
        HybridStorage.Atomic(Path.GetFullPath(receiptPath),
            JsonSerializer.Serialize(receipt, JsonIndented));
    }

    static IEnumerable<TelemetryRow> ReadTelemetry(string path)
    {
        using var file = File.OpenRead(path);
        using Stream stream = path.EndsWith(".gz", StringComparison.OrdinalIgnoreCase)
            ? new GZipStream(file, CompressionMode.Decompress)
            : file;
        using var reader = new StreamReader(stream);
        string? headerLine = reader.ReadLine();
        if (headerLine is null) yield break;
        var columns = Csv.Parse(headerLine).Select((name, index) => (name, index))
            .ToDictionary(pair => pair.name, pair => pair.index, StringComparer.Ordinal);
        while (reader.ReadLine() is { } line)
        {
            if (line.Length == 0) continue;
            string[] values;
            try { values = Csv.Parse(line); }
            catch (FormatException) { continue; }
            string Get(string name) => columns.TryGetValue(name, out int index) && index < values.Length
                ? values[index] : string.Empty;
            if (!DateTimeOffset.TryParse(Get("timestamp_utc"), CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind, out var timestamp)) continue;
            if (!DateTimeOffset.TryParse(Get("voltage_timestamp_utc"), CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind, out var voltageTimestamp))
                voltageTimestamp = default;
            yield return new TelemetryRow(timestamp.ToUniversalTime(),
                Get("gpu_uuid"), Get("voltage_source"), Get("analysis_power_source"),
                Get("electrical_source"), Get("electrical_freshness_kind"),
                Get("status"), Number(Get("input_voltage_v")),
                voltageTimestamp == default ? null : voltageTimestamp.ToUniversalTime(),
                Number(Get("analysis_power_w")), Number(Get("connector_power_w")),
                Number(Get("board_power_w")), Number(Get("gpu_temp_c")),
                Number(Get("sample_age_seconds")), Get("analysis_load_unit"),
                Get("reference_identity_json"), LongNumber(Get("poll_start_monotonic")));
        }
    }

    static bool IsAcceptedSourceRow(TelemetryRow row, ReferenceIdentity identity,
        Config config)
    {
        if (!string.Equals(row.GpuUuid, identity.GpuUuid, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(row.AnalysisPowerSource, identity.AnalysisLoadSource.WireName(),
                StringComparison.Ordinal) ||
            AnalysisLoadSourceExtensions.Parse(config.AnalysisLoadSource) != identity.AnalysisLoadSource)
            return false;

        string expectedUnit = identity.AnalysisLoadSource == AnalysisLoadSource.CONNECTOR_CURRENT
            ? "A" : "W";
        if (!string.IsNullOrWhiteSpace(row.AnalysisLoadUnit) &&
            !string.Equals(row.AnalysisLoadUnit, expectedUnit, StringComparison.OrdinalIgnoreCase))
            return false;
        if (!MatchesTelemetrySource(identity, row, row.VoltageSource, expectedUnit) ||
            !MatchesTelemetrySource(identity, row, row.ElectricalSource, expectedUnit))
            return false;

        if (string.IsNullOrWhiteSpace(row.ReferenceIdentityJson)) return true;
        return MeasurementIdentityCompatibility.MatchesMeasurement(
            identity.CanonicalJson, row.ReferenceIdentityJson);
    }

    static bool MatchesTelemetrySource(ReferenceIdentity identity, TelemetryRow row,
        string rowSource, string expectedUnit) =>
        MeasurementIdentityCompatibility.MatchesTelemetrySource(identity.GpuUuid,
            identity.Source, identity.AnalysisLoadSource.WireName(), row.GpuUuid,
            rowSource, row.AnalysisPowerSource, expectedUnit,
            out _, out _, out _);

    static bool IsFresh(TelemetryRow row, double maximumAgeSeconds) =>
        string.Equals(row.FreshnessKind, "HostPollTimestampUnverified", StringComparison.Ordinal) &&
        row.AgeSeconds is double age && double.IsFinite(age) && age >= 0 &&
        age <= maximumAgeSeconds;

    static DifferentialSample ToDifferentialSample(TelemetryRow row,
        ReferenceIdentity referenceIdentity, Config config, bool settled, bool fresh)
    {
        var identity = BuildDifferentialIdentity(referenceIdentity);
        return DifferentialSample.Create(row.Timestamp, row.Voltage,
            DifferentialFeatureVector.Create(connectorCurrentA: null,
                connectorPowerW: row.ConnectorPower,
                boardPowerW: row.BoardPower,
                temperatureC: row.Temperature),
            isFresh: fresh, isSynchronized: row.VoltageTimestamp is DateTimeOffset voltageTime &&
                Math.Abs((voltageTime - row.Timestamp).TotalSeconds) <= Math.Max(.5, config.SampleSeconds),
            isSettled: settled, ageSeconds: row.AgeSeconds,
            voltageTimestampUtc: row.VoltageTimestamp,
            featureTimestampUtc: row.Timestamp, identity: identity.CanonicalKey);
    }

    sealed record HoldoutBandSummary(int InputSamples, int PredictionSamples,
        double? ResidualMedianVolts, double? VoltageDeltaMedianVolts);

    sealed record HoldoutEvaluation(DifferentialModelRepairHoldoutReport Report,
        IReadOnlyDictionary<int, HoldoutBandSummary> Bands);

    sealed class MutableHoldoutBand
    {
        public int InputSamples;
        public List<double> Residuals { get; } = [];
        public List<double> VoltageDeltas { get; } = [];
    }

    static HoldoutEvaluation EvaluateHoldout(
        DifferentialModelArtifact artifact, AcceptedReferenceModel accepted, Config config,
        IReadOnlyList<TelemetryRow> rows, DateTimeOffset start, DateTimeOffset end)
    {
        var bins = accepted.Bins.ToDictionary(pair => pair.Key, pair => new Bin
        {
            Reference = pair.Value.ReferenceVolts,
            ReferenceP05 = pair.Value.ReferenceP05Volts,
        });
        var analysis = new Analysis(config, bins);
        var residuals = new List<double>();
        var reasons = new SortedDictionary<string, int>(StringComparer.Ordinal);
        var bandData = accepted.Bins.Keys.ToDictionary(key => key, _ => new MutableHoldoutBand());
        DateTimeOffset? previousSensorTimestamp = null;
        DateTimeOffset previousRowTimestamp = start;
        long? previousPollStart = null;
        double elapsedClock = 0;
        double gapResetSeconds = Math.Max(config.SampleSeconds * 3, config.MaxAgeSeconds * 2);
        int inputRows = 0;
        int unavailable = 0;
        int filteredIdentity = 0;
        foreach (var row in rows.Where(row => row.Timestamp > start && row.Timestamp <= end))
        {
            inputRows++;
            double rowGapSeconds = (row.Timestamp - previousRowTimestamp).TotalSeconds;
            elapsedClock += Math.Max(0, rowGapSeconds);
            if (rowGapSeconds > gapResetSeconds ||
                previousPollStart is long previousStart && row.PollStartMonotonic is long currentStart &&
                currentStart <= previousStart)
            {
                analysis.Gap();
                previousSensorTimestamp = null;
            }
            previousRowTimestamp = row.Timestamp;
            previousPollStart = row.PollStartMonotonic;
            if (!IsAcceptedSourceRow(row, accepted.Identity, config))
            {
                analysis.Gap();
                previousSensorTimestamp = null;
                filteredIdentity++;
                continue;
            }
            if (row.Voltage is not double voltage || row.VoltageTimestamp is not DateTimeOffset voltageTimestamp ||
                row.AnalysisPower is not double analysisPower)
            {
                analysis.Gap();
                previousSensorTimestamp = null;
                continue;
            }

            int? band = BinForLoad(analysisPower, config.BinWatts);
            if (band is int bandWatts && bandData.TryGetValue(bandWatts, out var bandMetrics))
            {
                bandMetrics.InputSamples++;
                bandMetrics.VoltageDeltas.Add(voltage - accepted.Bins[bandWatts].ReferenceVolts!.Value);
            }
            if (previousSensorTimestamp.HasValue && voltageTimestamp <= previousSensorTimestamp.Value)
            {
                analysis.Gap();
                previousSensorTimestamp = null;
                continue;
            }
            previousSensorTimestamp = voltageTimestamp;
            bool fresh = IsFresh(row, config.MaxAgeSeconds);
            var result = analysis.Add(voltageTimestamp, analysisPower, voltage,
                elapsedClock, fresh);
            var sample = ToDifferentialSample(row, accepted.Identity, config,
                result.LoadQualification?.IsQualified == true, fresh);
            var prediction = artifact.Predict(sample);
            if (prediction.IsAvailable && prediction.ResidualVolts is double residual)
            {
                residuals.Add(residual);
                if (band is int predictionBand && bandData.TryGetValue(predictionBand, out bandMetrics))
                    bandMetrics.Residuals.Add(residual);
            }
            else
            {
                unavailable++;
                foreach (var reason in prediction.Qualification.Reasons)
                {
                    string key = reason.ToString();
                    reasons[key] = reasons.TryGetValue(key, out var count) ? count + 1 : 1;
                }
            }
        }
        var summaries = bandData.ToDictionary(pair => pair.Key, pair => new HoldoutBandSummary(
            pair.Value.InputSamples, pair.Value.Residuals.Count,
            Median(pair.Value.Residuals), Median(pair.Value.VoltageDeltas)));
        var report = new DifferentialModelRepairHoldoutReport(start, end, inputRows,
            residuals.Count, unavailable, filteredIdentity,
            residuals.Count == 0 ? null : Analysis.Percentile(residuals, .05),
            residuals.Count == 0 ? null : Analysis.Percentile(residuals, .5),
            residuals.Count == 0 ? null : Analysis.Percentile(residuals, .95), reasons);
        return new(report, summaries);
    }

    static IReadOnlyList<DifferentialModelRepairBinCheck> CompareAcceptedBins(
        DifferentialModelArtifact artifact, AcceptedReferenceModel accepted,
        IReadOnlyList<DifferentialSample> samples, int binWatts)
    {
        var checks = new List<DifferentialModelRepairBinCheck>();
        foreach (var pair in accepted.Bins.OrderBy(pair => pair.Key))
        {
            var inBin = samples.Where(sample => BinForLoad(
                sample.Features.GetLoad(artifact.LoadProxy), binWatts) == pair.Key).ToArray();
            if (inBin.Length == 0)
            {
                checks.Add(new(pair.Key, pair.Value.ReferenceVolts!.Value, null, null, 0,
                    false, "No training samples fall in this accepted load band."));
                continue;
            }
            var medianBoard = Median(inBin.Select(sample => sample.Features.BoardPowerW));
            var medianTemperature = Median(inBin.Select(sample => sample.Features.TemperatureC));
            var probe = DifferentialSample.Create(inBin[inBin.Length / 2].TimestampUtc,
                pair.Value.ReferenceVolts,
                DifferentialFeatureVector.Create(connectorPowerW: pair.Key + binWatts / 2.0,
                    boardPowerW: medianBoard, temperatureC: medianTemperature),
                isFresh: true, isSynchronized: true, isSettled: true, ageSeconds: 0,
                identity: artifact.Identity.CanonicalKey);
            var prediction = artifact.Predict(probe);
            checks.Add(new(pair.Key, pair.Value.ReferenceVolts!.Value,
                prediction.ExpectedVoltageV,
                prediction.ExpectedVoltageV is double expected
                    ? expected - pair.Value.ReferenceVolts.Value : null,
                inBin.Length, prediction.IsAvailable,
                prediction.IsAvailable ? prediction.Detail : prediction.Detail));
        }
        return checks;
    }

    static IReadOnlyList<DifferentialModelRepairBandReport> BuildBandReports(
        DifferentialModelArtifact artifact, AcceptedReferenceModel accepted,
        IReadOnlyList<DifferentialSample> allTrainingSamples,
        IReadOnlyList<DifferentialSample> fitSamples,
        IReadOnlyDictionary<int, HoldoutBandSummary> holdoutBands, int binWatts)
    {
        var loadFeature = artifact.Coefficients.FirstOrDefault(coefficient =>
            coefficient.Feature == DifferentialFeatureKind.LOAD_PROXY);
        var reports = new List<DifferentialModelRepairBandReport>();
        foreach (var pair in accepted.Bins.OrderBy(pair => pair.Key))
        {
            double reference = pair.Value.ReferenceVolts!.Value;
            var training = allTrainingSamples.Where(sample => BinForLoad(
                sample.Features.GetLoad(artifact.LoadProxy), binWatts) == pair.Key).ToArray();
            var fitted = fitSamples.Where(sample => BinForLoad(
                sample.Features.GetLoad(artifact.LoadProxy), binWatts) == pair.Key).ToArray();
            var trainingResiduals = fitted.Select(sample => artifact.Predict(sample))
                .Where(prediction => prediction.IsAvailable && prediction.ResidualVolts.HasValue)
                .Select(prediction => prediction.ResidualVolts!.Value).ToArray();
            var trainingVoltageDeltas = training.Where(sample => sample.InputVoltageV.HasValue)
                .Select(sample => sample.InputVoltageV!.Value - reference).ToArray();
            double? medianBoard = Median(fitted.Select(sample => sample.Features.BoardPowerW));
            double? medianTemperature = Median(fitted.Select(sample => sample.Features.TemperatureC));
            double? center = null;
            if (medianBoard is double board && medianTemperature is double temperature)
            {
                var probe = DifferentialSample.Create(DateTimeOffset.UnixEpoch, reference,
                    DifferentialFeatureVector.Create(
                        connectorCurrentA: artifact.LoadProxy == DifferentialLoadProxy.CONNECTOR_CURRENT
                            ? pair.Key + binWatts / 2.0 : null,
                        connectorPowerW: artifact.LoadProxy == DifferentialLoadProxy.CONNECTOR_POWER
                            ? pair.Key + binWatts / 2.0 : null,
                        boardPowerW: artifact.LoadProxy == DifferentialLoadProxy.NVML_BOARD_POWER
                            ? pair.Key + binWatts / 2.0 : board,
                        temperatureC: temperature,
                        externalSensorPowerW: artifact.LoadProxy == DifferentialLoadProxy.EXTERNAL_SENSOR_POWER
                            ? pair.Key + binWatts / 2.0 : null),
                    isFresh: true, isSynchronized: true, isSettled: true, ageSeconds: 0,
                    identity: artifact.Identity.CanonicalKey);
                var prediction = artifact.Predict(probe);
                if (prediction.IsAvailable) center = prediction.ExpectedVoltageV;
            }

            holdoutBands.TryGetValue(pair.Key, out var holdout);
            bool envelopeCovers = loadFeature is not null &&
                loadFeature.Envelope.Minimum <= pair.Key &&
                loadFeature.Envelope.Maximum >= pair.Key + binWatts;
            reports.Add(new(pair.Key, reference, training.Length, fitted.Length,
                envelopeCovers, center, center is double expected ? expected - reference : null,
                Median(trainingResiduals), Median(trainingVoltageDeltas),
                holdout?.InputSamples ?? 0, holdout?.PredictionSamples ?? 0,
                holdout?.ResidualMedianVolts, holdout?.VoltageDeltaMedianVolts));
        }
        return reports;
    }

    static IReadOnlyList<DifferentialSample> SelectTrainingSamples(
        IReadOnlyList<DifferentialSample> samples, DifferentialLoadProxy loadProxy,
        int binWatts, int maximumSamples)
    {
        var ordered = samples.OrderBy(sample => sample.TimestampUtc).ToArray();
        if (ordered.Length <= maximumSamples) return ordered;
        var groups = ordered.GroupBy(sample => BinForLoad(
                sample.Features.GetLoad(loadProxy), binWatts) ?? int.MinValue)
            .OrderBy(group => group.Key)
            .Select(group => group.ToArray()).ToArray();
        var targets = new int[groups.Length];
        int remaining = maximumSamples;
        while (remaining > 0)
        {
            bool allocated = false;
            for (int index = 0; index < groups.Length && remaining > 0; index++)
            {
                if (targets[index] >= groups[index].Length) continue;
                targets[index]++;
                remaining--;
                allocated = true;
            }
            if (!allocated) break;
        }

        var selected = new List<DifferentialSample>(maximumSamples);
        for (int groupIndex = 0; groupIndex < groups.Length; groupIndex++)
        {
            var group = groups[groupIndex];
            int target = targets[groupIndex];
            for (int index = 0; index < target; index++)
            {
                int sourceIndex = Math.Min(group.Length - 1,
                    (int)Math.Floor((index + .5) * group.Length / target));
                selected.Add(group[sourceIndex]);
            }
        }
        return selected.OrderBy(sample => sample.TimestampUtc).ToArray();
    }

    static int? BinForLoad(double? load, int binWatts) =>
        load is double value && double.IsFinite(value) && value >= 0
            ? (int)Math.Floor(value / binWatts) * binWatts : null;

    static double? Median(IEnumerable<double?> numbers)
    {
        var values = numbers.Where(value => value is double number && double.IsFinite(number))
            .Select(value => value!.Value).Order().ToArray();
        return values.Length == 0 ? null : Analysis.Percentile(values, .5);
    }

    static double? Median(IEnumerable<double> numbers)
    {
        var values = numbers.Where(double.IsFinite).Order().ToArray();
        return values.Length == 0 ? null : Analysis.Percentile(values, .5);
    }

    static void ValidateConfigBinding(Config config, ReferenceIdentity identity)
    {
        var qualification = identity.Qualification;
        bool matches = AnalysisLoadSourceExtensions.Parse(config.AnalysisLoadSource) ==
                identity.AnalysisLoadSource &&
            qualification.BinWatts == config.BinWatts &&
            qualification.MinAnalysisWatts == config.MinAnalysisWatts &&
            qualification.StableSamples == config.StableSamples &&
            qualification.BaselineSamples == config.BaselineSamples &&
            qualification.WindowSamples == config.WindowSamples &&
            qualification.WindowMaxAgeSeconds == config.WindowMaxAgeSeconds &&
            qualification.MaxAgeSeconds == config.MaxAgeSeconds &&
            qualification.SampleSeconds == config.SampleSeconds &&
            qualification.ShiftVolts == config.ShiftVolts &&
            qualification.SuddenDroopVolts == config.SuddenDroopVolts &&
            qualification.SustainSamples == config.SustainSamples &&
            qualification.LoadBoundaryHysteresisWatts == config.LoadBoundaryHysteresisWatts &&
            qualification.CoarseConfirmationSeconds == config.CoarseConfirmationSeconds &&
            qualification.CoarseConfirmationSamples == config.CoarseConfirmationSamples &&
            qualification.GrossUnderVoltageV == config.GrossUnderVoltageV &&
            qualification.GrossOverVoltageV == config.GrossOverVoltageV;
        if (!matches)
            throw new InvalidDataException("Captured config does not match the accepted reference qualification identity.");
    }

    static DifferentialModelIdentity BuildDifferentialIdentity(ReferenceIdentity identity) =>
        new(gpuUuid: identity.GpuUuid, board: identity.Board, driver: identity.Driver,
            voltageSource: identity.Source, configurationId: identity.VersionedKey);

    static IEnumerable<string> FindTelemetryFiles(string root, DateTimeOffset end)
    {
        var endDate = DateOnly.FromDateTime(end.UtcDateTime);
        foreach (var path in Directory.EnumerateFiles(root, "telemetry-*")
                     .OrderBy(path => Path.GetFileName(path), StringComparer.Ordinal))
        {
            string name = Path.GetFileName(path);
            if (name.Length < 21 || !DateOnly.TryParseExact(name.Substring(10, 10),
                    "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None,
                    out var fileDate)) continue;
            if (fileDate <= endDate) yield return path;
        }
    }

    internal static string ComputePackageHash(DifferentialModelRepairPackage package)
    {
        var payload = new
        {
            schema_version = package.SchemaVersion,
            artifact_json = package.ArtifactJson,
            provenance = package.Provenance,
            report = package.Report,
        };
        return Sha256(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload, JsonCompact)));
    }

    static string Sha256File(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    static string Sha256(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));

    static double? Number(string text) => double.TryParse(text, NumberStyles.Float,
        CultureInfo.InvariantCulture, out var value) && double.IsFinite(value) ? value : null;

    static long? LongNumber(string text) => long.TryParse(text, NumberStyles.Integer,
        CultureInfo.InvariantCulture, out var value) ? value : null;

    static string? Option(string[] args, string name)
    {
        int index = Array.IndexOf(args, name);
        return index < 0 ? null : index + 1 < args.Length
            ? args[index + 1] : throw new ArgumentException("Missing value for " + name);
    }

    static string ResolveCaptureOutput(string root, string? requested, string defaultName)
    {
        string path = Path.GetFullPath(requested ?? Path.Combine(root, defaultName));
        if (!DeploymentPaths.IsWithin(path, Path.GetFullPath(root)))
            throw new ArgumentException("Repair output files must remain inside the captured evidence directory.");
        return path;
    }

    static void ValidateConfigBinding(Config config, ReferenceIdentity identity,
        DifferentialModelOptions options)
    {
        ValidateConfigBinding(config, identity);
        if (!BuildDifferentialIdentity(identity).Matches(options.Identity))
            throw new InvalidDataException("Differential model identity does not match the accepted reference.");
    }

    static ControlResponse? SendControlRequest(string dataDirectory, ControlRequest request)
    {
        using var pipe = new NamedPipeClientStream(".", ControlEndpoint.Name(dataDirectory),
            PipeDirection.InOut, PipeOptions.CurrentUserOnly);
        pipe.Connect(2000);
        using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 1024, leaveOpen: true)
        { AutoFlush = true };
        writer.WriteLine(ControlProtocol.Serialize(request));
        using var reader = new StreamReader(pipe, Encoding.UTF8, false, 1024, leaveOpen: true);
        string? line = reader.ReadLine();
        return string.IsNullOrWhiteSpace(line)
            ? null : JsonSerializer.Deserialize<ControlResponse>(line, ControlProtocol.Json);
    }

    sealed record TelemetryRow(
        DateTimeOffset Timestamp,
        string GpuUuid,
        string VoltageSource,
        string AnalysisPowerSource,
        string ElectricalSource,
        string FreshnessKind,
        string Status,
        double? Voltage,
        DateTimeOffset? VoltageTimestamp,
        double? AnalysisPower,
        double? ConnectorPower,
        double? BoardPower,
        double? Temperature,
        double? AgeSeconds,
        string AnalysisLoadUnit,
        string ReferenceIdentityJson,
        long? PollStartMonotonic);
}
