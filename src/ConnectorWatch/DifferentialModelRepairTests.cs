using System.Text.Json;
using System.Text.Json.Serialization;

namespace ConnectorWatch;

public static class DifferentialModelRepairTests
{
    static readonly JsonSerializerOptions PackageJson = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static void Run()
    {
        string root = Path.Combine(AppContext.BaseDirectory,
            "ConnectorWatch-model-repair-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var fixture = CreateFixture();
            string previousJson = DifferentialModelPersistence.Serialize(fixture.PreviousArtifact);
            string packageJson = JsonSerializer.Serialize(fixture.Package, PackageJson);

            SuccessfulImportAfterRestartPreservesReference(root, fixture, previousJson, packageJson);
            RejectsUnfittedCandidate(root, fixture, previousJson);
            RejectsReferenceAndOptionsMismatch(root, fixture, previousJson);
            RejectsChangedOldArtifact(root, fixture, previousJson);
            RejectsReplacementOfFittedArtifact(root, fixture);
            RejectsMissingActiveArtifact(root, fixture, previousJson);
            RejectsPostAcceptanceTraining(root, fixture, previousJson);
            RejectsChangedTrainingProvenance(root, fixture, previousJson);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
        Console.WriteLine("PASS: differential model repair preparation and guarded import checks.");
    }

    static void SuccessfulImportAfterRestartPreservesReference(string root, Fixture fixture,
        string previousJson, string packageJson)
    {
        string folder = CaseFolder(root, "restart-success");
        string modelPath = Path.Combine(folder, "differential-model.json");
        string referencePath = Path.Combine(folder, "reference.json");
        string baselinePath = Path.Combine(folder, "baseline.json");
        string candidatePath = Path.Combine(folder, "candidate.json");
        string receiptPath = Path.Combine(folder, "receipt.json");
        string referenceContents = JsonSerializer.Serialize(fixture.Accepted);
        const string baselineContents = "{\"legacy_reference\":\"keep\"}";
        File.WriteAllText(modelPath, previousJson);
        File.WriteAllText(referencePath, referenceContents);
        File.WriteAllText(baselinePath, baselineContents);
        File.WriteAllText(candidatePath, packageJson);

        // Loading a new runtime from the persisted insufficient artifact models a daemon restart.
        var restartedRuntime = new DifferentialModelRuntime(fixture.Options,
            new ResidualDetectorOptions());
        restartedRuntime.Load(DifferentialModelPersistence.Deserialize(File.ReadAllText(modelPath)));
        var imported = DifferentialModelRepairCommand.ImportPackage(candidatePath,
            modelPath, receiptPath, fixture.Accepted, restartedRuntime.Artifact,
            fixture.Options, fixture.Accepted.AcceptedAtUtc.AddMinutes(1));
        restartedRuntime.Load(imported);

        Check(imported.Diagnostics.State == DifferentialModelState.FITTED && imported.IsFitted,
            "successful import contains a fitted artifact");
        Check(restartedRuntime.Artifact?.ArtifactHash == imported.ArtifactHash,
            "restarted runtime activates imported fitted artifact");
        Check(DifferentialModelPersistence.Deserialize(File.ReadAllText(modelPath)).ArtifactHash ==
            imported.ArtifactHash, "successful import persists the fitted artifact");
        Check(File.ReadAllText(referencePath) == referenceContents &&
            File.ReadAllText(baselinePath) == baselineContents,
            "successful import preserves accepted reference and baseline files");
        using var receipt = JsonDocument.Parse(File.ReadAllText(receiptPath));
        Check(receipt.RootElement.GetProperty("status").GetString() == "IMPORTED",
            "successful import writes an imported receipt");
    }

    static void RejectsUnfittedCandidate(string root, Fixture fixture, string previousJson)
    {
        var badCandidate = DifferentialModelTrainer.Fit(Array.Empty<DifferentialSample>(),
            fixture.Options with { ArtifactCreatedAtUtc = fixture.Accepted.AcceptedAtUtc }).Artifact;
        var provenance = fixture.Package.Provenance with
        {
            CandidateArtifactHash = badCandidate.ArtifactHash,
        };
        var unhashed = fixture.Package with
        {
            ArtifactJson = DifferentialModelPersistence.Serialize(badCandidate),
            Provenance = provenance,
            PackageSha256 = string.Empty,
        };
        var package = unhashed with
        {
            PackageSha256 = DifferentialModelRepairCommand.ComputePackageHash(unhashed),
        };
        RejectImport(root, "unfitted-candidate", package, fixture.Accepted,
            fixture.Options, previousJson, fixture.PreviousArtifact,
            "FITTED artifact");
    }

    static void RejectsReferenceAndOptionsMismatch(string root, Fixture fixture,
        string previousJson)
    {
        var changedReference = new AcceptedReferenceModel(fixture.Accepted.Identity,
            new Dictionary<int, ReferenceBinStatistics>
            {
                [125] = new(125, 12.01, 11.99, 100, 100),
            }, fixture.Accepted.AcceptedAtUtc, "fixture");
        RejectImport(root, "changed-reference", fixture.Package, changedReference,
            fixture.Options, previousJson, fixture.PreviousArtifact,
            "accepted reference changed");
        RejectImport(root, "changed-options", fixture.Package, fixture.Accepted,
            fixture.Options with { IncludeTemperature = false }, previousJson,
            fixture.PreviousArtifact, "identity or settings");
    }

    static void RejectsChangedOldArtifact(string root, Fixture fixture, string previousJson)
    {
        RejectImport(root, "changed-old-artifact", fixture.Package, fixture.Accepted,
            fixture.Options, previousJson + " ", fixture.PreviousArtifact,
            "active differential artifact changed");
    }

    static void RejectsReplacementOfFittedArtifact(string root, Fixture fixture)
    {
        var package = DifferentialModelRepairCommand.CreatePackage(fixture.Fit,
            fixture.Fit.Artifact, fixture.Accepted, fixture.Options,
            fixture.Package.Report);
        string fittedJson = DifferentialModelPersistence.Serialize(fixture.Fit.Artifact);
        RejectImport(root, "fitted-active-artifact", package, fixture.Accepted,
            fixture.Options, fittedJson, fixture.Fit.Artifact,
            "fitted active differential artifact");
    }

    static void RejectsMissingActiveArtifact(string root, Fixture fixture, string previousJson)
    {
        var withoutPrevious = fixture.Package with
        {
            Provenance = fixture.Package.Provenance with
            {
                PreviousArtifactSha256 = null,
                PreviousArtifactHash = null,
            },
            PackageSha256 = string.Empty,
        };
        withoutPrevious = withoutPrevious with
        {
            PackageSha256 = DifferentialModelRepairCommand.ComputePackageHash(withoutPrevious),
        };
        RejectImport(root, "missing-active-artifact", withoutPrevious, fixture.Accepted,
            fixture.Options, null, null, "existing matching INSUFFICIENT_DATA artifact");
        RejectImport(root, "missing-runtime-artifact", fixture.Package, fixture.Accepted,
            fixture.Options, previousJson, null, "existing matching INSUFFICIENT_DATA artifact");
    }

    static void RejectsPostAcceptanceTraining(string root, Fixture fixture, string previousJson)
    {
        var original = fixture.Fit.Artifact;
        var postAcceptance = new DifferentialModelArtifact(original.SchemaVersion,
            original.AlgorithmVersion, original.FeatureVersion, original.LoadProxy,
            original.Coefficients, original.InterceptVolts, original.ApparentSlopeVoltsPerUnit,
            original.SlopeUnit, original.Diagnostics, original.Identity, original.Settings,
            fixture.Accepted.AcceptedAtUtc.AddSeconds(1), fixture.Accepted.AcceptedAtUtc.AddSeconds(2),
            fixture.Accepted.AcceptedAtUtc);
        var fit = fixture.Fit with { Artifact = postAcceptance };
        var report = fixture.Package.Report with
        {
            TrainingEndUtc = postAcceptance.TrainingEndUtc,
            ArtifactHash = postAcceptance.ArtifactHash,
        };
        var package = DifferentialModelRepairCommand.CreatePackage(fit,
            fixture.PreviousArtifact, fixture.Accepted, fixture.Options, report);
        RejectImport(root, "post-acceptance-training", package, fixture.Accepted,
            fixture.Options, previousJson, fixture.PreviousArtifact,
            "training must precede reference acceptance");
    }

    static void RejectsChangedTrainingProvenance(string root, Fixture fixture, string previousJson)
    {
        var package = fixture.Package with
        {
            Provenance = fixture.Package.Provenance with
            {
                TrainingEndUtc = fixture.Package.Provenance.TrainingEndUtc.AddSeconds(1),
            },
            PackageSha256 = string.Empty,
        };
        package = package with { PackageSha256 = DifferentialModelRepairCommand.ComputePackageHash(package) };
        RejectImport(root, "changed-training-provenance", package, fixture.Accepted,
            fixture.Options, previousJson, fixture.PreviousArtifact,
            "training timestamps do not match");
    }

    static void RejectImport(string root, string name,
        DifferentialModelRepairPackage package, AcceptedReferenceModel accepted,
        DifferentialModelOptions options, string? activeJson,
        DifferentialModelArtifact? runtimeArtifact, string expectedMessage)
    {
        string folder = CaseFolder(root, name);
        string modelPath = Path.Combine(folder, "differential-model.json");
        string candidatePath = Path.Combine(folder, "candidate.json");
        string receiptPath = Path.Combine(folder, "receipt.json");
        if (activeJson is not null) File.WriteAllText(modelPath, activeJson);
        File.WriteAllText(candidatePath, JsonSerializer.Serialize(package, PackageJson));
        bool rejected = false;
        try
        {
            _ = DifferentialModelRepairCommand.ImportPackage(candidatePath, modelPath,
                receiptPath, accepted, runtimeArtifact, options,
                accepted.AcceptedAtUtc.AddMinutes(1));
        }
        catch (InvalidDataException ex)
        {
            rejected = ex.Message.Contains(expectedMessage, StringComparison.OrdinalIgnoreCase);
        }
        Check(rejected, $"{name} is rejected with the expected guard");
        Check((activeJson is null ? !File.Exists(modelPath) : File.ReadAllText(modelPath) == activeJson) &&
            !File.Exists(receiptPath),
            $"{name} leaves the active model and receipt unchanged");
    }

    static Fixture CreateFixture()
    {
        var config = new Config
        {
            GpuUuid = "GPU-REPAIR-FIXTURE",
            AnalysisLoadSource = "CONNECTOR_POWER",
        };
        var identity = Program.BuildReferenceIdentity(config, "json",
            "fixture rail source", AnalysisLoadSource.CONNECTOR_POWER);
        var options = Program.BuildDifferentialOptions(config, identity) with
        {
            MinimumSamples = 8,
            MinimumSlopeSamples = 8,
            MinimumLoadSpan = 50,
        };
        var acceptedAt = new DateTimeOffset(2026, 2, 1, 0, 0, 0, TimeSpan.Zero);
        var accepted = new AcceptedReferenceModel(identity,
            new Dictionary<int, ReferenceBinStatistics>
            {
                [125] = new(125, 12.0, 11.99, 100, 100),
            }, acceptedAt, "fixture", "repair test");
        var samples = new List<DifferentialSample>();
        var start = acceptedAt.AddDays(-1);
        for (int index = 0; index < 36; index++)
        {
            var timestamp = start.AddSeconds(index);
            double load = 125 + index * 6;
            double boardPower = 270 + index * 4 + (index % 3) * 2;
            double temperature = 45 + index * .17 + (index % 4) * .5;
            double voltage = 12.25 - .001 * load + .0005 * boardPower - .002 * temperature;
            samples.Add(DifferentialSample.Create(timestamp, voltage,
                DifferentialFeatureVector.Create(connectorPowerW: load,
                    boardPowerW: boardPower, temperatureC: temperature),
                isFresh: true, isSynchronized: true, isSettled: true,
                ageSeconds: .2, voltageTimestampUtc: timestamp,
                featureTimestampUtc: timestamp,
                identity: Program.BuildDifferentialIdentity(identity).CanonicalKey));
        }
        var fitOptions = options with { ArtifactCreatedAtUtc = acceptedAt };
        var fit = DifferentialModelTrainer.Fit(samples, fitOptions);
        if (!fit.IsUsable || fit.Artifact.Diagnostics.State != DifferentialModelState.FITTED)
            throw new InvalidOperationException("Repair fixture did not produce a fitted model: " + fit.Detail);
        var previous = DifferentialModelTrainer.Fit(Array.Empty<DifferentialSample>(),
            fitOptions).Artifact;
        var report = BuildReport(fit, accepted, samples);
        var package = DifferentialModelRepairCommand.CreatePackage(fit, previous,
            accepted, options, report);
        return new(options, accepted, fit, previous, package);
    }

    static DifferentialModelRepairReport BuildReport(DifferentialModelFitResult fit,
        AcceptedReferenceModel accepted, IReadOnlyList<DifferentialSample> samples)
    {
        var diagnostics = fit.Artifact.Diagnostics;
        var holdout = new DifferentialModelRepairHoldoutReport(
            accepted.AcceptedAtUtc, accepted.AcceptedAtUtc.AddHours(24),
            0, 0, 0, 0, null, null, null, new Dictionary<string, int>());
        var first = samples[0].TimestampUtc;
        var last = samples[^1].TimestampUtc;
        return new(accepted.AcceptedAtUtc, first,
            "compatible_preacceptance_telemetry_reconstruction", "REFERENCE_UNVERIFIED",
            first, first, first, last, samples.Count, samples.Count,
            DifferentialModelRepairCommand.MaximumTrainingSamples, true, 0, 0, 0,
            diagnostics.State.ToString(), diagnostics.InputSamples,
            diagnostics.QualifiedSamples, diagnostics.RejectedSamples,
            diagnostics.EffectiveRank, diagnostics.ConditionNumber,
            diagnostics.LoadSpan, diagnostics.RootMeanSquareErrorVolts,
            diagnostics.MedianAbsoluteErrorVolts, fit.Artifact.ArtifactHash,
            Array.Empty<DifferentialModelRepairBinCheck>(),
            Array.Empty<DifferentialModelRepairBandReport>(), holdout);
    }

    static string CaseFolder(string root, string name)
    {
        string folder = Path.Combine(root, name);
        Directory.CreateDirectory(folder);
        return folder;
    }

    static void Check(bool condition, string description)
    {
        if (!condition) throw new Exception("FAILED: " + description);
    }

    sealed record Fixture(DifferentialModelOptions Options,
        AcceptedReferenceModel Accepted,
        DifferentialModelFitResult Fit,
        DifferentialModelArtifact PreviousArtifact,
        DifferentialModelRepairPackage Package);
}
