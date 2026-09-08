using System.Text.Json;

namespace ConnectorWatch;

/// <summary>
/// Offline checks for the isolated differential model.  The daemon self-test
/// can invoke <see cref="Run"/> when this feature is wired into a release; the
/// checks deliberately use no GPU, clock, file, or network state.
/// </summary>
public static class DifferentialModelTests
{
    public static void Run()
    {
        RobustFitReportsExpectedResidualAndSlope();
        QualificationGatesAreExplicit();
        EnvelopeAndIdentityAreFailClosed();
        PoorCoverageLeavesSlopeNull();
        ArtifactRoundTripIsVersionedAndHashed();
    }

    static DifferentialModelFitResult FitFixture(
        IReadOnlyList<DifferentialSample> samples,
        DifferentialModelOptions? overrides = null)
    {
        var defaults = new DifferentialModelOptions
        {
            LoadProxy = DifferentialLoadProxy.CONNECTOR_POWER,
            IncludeBoardPower = true,
            IncludeTemperature = true,
            IncludeFanPercent = true,
            IncludeThermalState = true,
            MinimumSamples = 8,
            MinimumSlopeSamples = 8,
            MinimumLoadSpan = 50,
            EnvelopeMarginFraction = 0,
            Identity = new DifferentialModelIdentity(
                gpuUuid: "GPU-FIXTURE",
                board: "BOARD-FIXTURE",
                driver: "DRIVER-FIXTURE",
                voltageSource: "source-fixture",
                configurationId: "config-fixture"),
        };
        if (overrides is null) return DifferentialModelTrainer.Fit(samples, defaults);
        return DifferentialModelTrainer.Fit(samples, overrides);
    }

    static IReadOnlyList<DifferentialSample> FixtureSamples(
        int count = 24, bool includeIdentity = true)
    {
        var identity = new DifferentialModelIdentity(
            gpuUuid: "GPU-FIXTURE",
            board: "BOARD-FIXTURE",
            driver: "DRIVER-FIXTURE",
            voltageSource: "source-fixture",
            configurationId: "config-fixture").CanonicalKey;
        var list = new List<DifferentialSample>(count);
        var start = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        for (int i = 0; i < count; i++)
        {
            double power = 250 + i * 25;
            // Keep covariates related to load, but not exact linear copies:
            // the fit must exercise conditioning rather than rely on a
            // singular fixture.
            double boardPower = 270 + i * 24 + (i % 3) * 2;
            double temperature = 45 + i * .15 + (i % 4) * .7;
            double fan = 30 + i * .3 + (i % 5) * .9;
            double thermal = i < count / 2 ? 0 : 1;
            // Chosen coefficients are deliberately simple in raw units.
            double voltage = 12.25 - .001 * power + .0005 * boardPower -
                .002 * temperature + .0004 * fan - .025 * thermal;
            list.Add(new DifferentialSample(start.AddSeconds(i), voltage,
                new DifferentialFeatureVector(
                    connectorPowerW: power,
                    boardPowerW: boardPower,
                    temperatureC: temperature,
                    fanPercent: fan,
                    thermalStateValue: thermal),
                ageSeconds: .2,
                voltageTimestampUtc: start.AddSeconds(i),
                featureTimestampUtc: start.AddSeconds(i),
                identity: includeIdentity ? identity : null));
        }
        return list;
    }

    static void RobustFitReportsExpectedResidualAndSlope()
    {
        var rows = FixtureSamples().ToList();
        // A gross outlier must not become the fitted voltage law.
        var outlier = rows[12];
        rows[12] = outlier with { InputVoltageV = outlier.InputVoltageV!.Value - 2.0 };
        var fit = FitFixture(rows);
        Check(fit.Artifact.Diagnostics.QualifiedSamples == rows.Count,
            "robust fixture admits all qualified samples");
        Check(fit.Artifact.Diagnostics.State == DifferentialModelState.FITTED,
            "robust fixture is fitted");
        Check(fit.Artifact.ApparentSlopeVoltsPerUnit is double slope &&
            slope < -.0001 && slope > -.005,
            "Huber apparent load slope is not dominated by an outlier");

        var probe = rows[5] with { InputVoltageV = rows[5].InputVoltageV!.Value - .18 };
        var prediction = fit.Artifact.Predict(probe);
        Check(prediction.IsAvailable && prediction.ExpectedVoltageV.HasValue,
            "qualified prediction reports expected voltage");
        Check(prediction.ResidualVolts is double residual && residual < -.15,
            "prediction reports signed residual");
        Check(prediction.ExcessDroopVolts is double excess && excess > .15,
            "prediction reports positive excess droop");
        Check(prediction.ApparentSlopeVoltsPerUnit == fit.Artifact.ApparentSlopeVoltsPerUnit,
            "prediction slope comes from frozen qualified fit");
    }

    static void QualificationGatesAreExplicit()
    {
        var rows = FixtureSamples().ToList();
        var baseRow = rows[0];
        rows.Add(baseRow with { TimestampUtc = baseRow.TimestampUtc.AddSeconds(100), IsFresh = false });
        rows.Add(baseRow with { TimestampUtc = baseRow.TimestampUtc.AddSeconds(101), IsSynchronized = false });
        rows.Add(baseRow with { TimestampUtc = baseRow.TimestampUtc.AddSeconds(102), IsSettled = false });
        rows.Add(baseRow with { TimestampUtc = baseRow.TimestampUtc.AddSeconds(103), AgeSeconds = 99 });
        rows.Add(baseRow with { TimestampUtc = baseRow.TimestampUtc.AddSeconds(104),
            InputVoltageV = double.NaN });
        rows.Add(baseRow with { TimestampUtc = baseRow.TimestampUtc.AddSeconds(105),
            Features = baseRow.Features with { ConnectorPowerW = 20_000 } });
        var fit = FitFixture(rows);
        Check(fit.Artifact.Diagnostics.QualifiedSamples == 24,
            "only gate-clean rows enter the fit");
        Check(fit.Artifact.Diagnostics.Rejections.ContainsKey(DifferentialSampleRejectionReason.NOT_FRESH) &&
            fit.Artifact.Diagnostics.Rejections.ContainsKey(DifferentialSampleRejectionReason.NOT_SYNCHRONIZED) &&
            fit.Artifact.Diagnostics.Rejections.ContainsKey(DifferentialSampleRejectionReason.NOT_SETTLED) &&
            fit.Artifact.Diagnostics.Rejections.ContainsKey(DifferentialSampleRejectionReason.AGE_EXCEEDED) &&
            fit.Artifact.Diagnostics.Rejections.ContainsKey(DifferentialSampleRejectionReason.NON_FINITE) &&
            fit.Artifact.Diagnostics.Rejections.ContainsKey(DifferentialSampleRejectionReason.OUT_OF_RANGE),
            "fresh, synchronized, finite, in-range, and settled gates are diagnosed");
        Check(fit.Artifact.Predict(rows[^1]).Qualification.Reasons.Contains(
            DifferentialSampleRejectionReason.OUT_OF_RANGE),
            "out-of-range prediction is unavailable rather than imputed");
    }

    static void EnvelopeAndIdentityAreFailClosed()
    {
        var fit = FitFixture(FixtureSamples());
        var outside = FixtureSamples(1)[0] with
        {
            Features = FixtureSamples(1)[0].Features with { ConnectorPowerW = 1_000 }
        };
        var result = fit.Artifact.Predict(outside);
        Check(!result.IsAvailable && result.ExpectedVoltageV is null &&
            result.Qualification.Reasons.Contains(DifferentialSampleRejectionReason.OUTSIDE_ENVELOPE),
            "out-of-envelope prediction is unavailable");

        var wrongIdentity = FixtureSamples(1)[0] with { Identity = "v1|gpu=other" };
        var identityResult = fit.Artifact.Predict(wrongIdentity);
        Check(!identityResult.IsAvailable && identityResult.Qualification.Reasons.Contains(
            DifferentialSampleRejectionReason.IDENTITY_MISMATCH),
            "identity mismatch is fail-closed");
    }

    static void PoorCoverageLeavesSlopeNull()
    {
        var rows = FixtureSamples().Select(x => x with
        {
            Features = x.Features with { ConnectorPowerW = 500 }
        }).ToList();
        var fit = FitFixture(rows);
        Check(fit.Artifact.ApparentSlopeVoltsPerUnit is null,
            "constant load coverage leaves apparent slope null");
        Check(fit.Artifact.Diagnostics.State == DifferentialModelState.ILL_CONDITIONED,
            "constant load coverage is diagnosed as ill-conditioned");
    }

    static void ArtifactRoundTripIsVersionedAndHashed()
    {
        var fit = FitFixture(FixtureSamples());
        string json = DifferentialModelPersistence.Serialize(fit.Artifact);
        var restored = DifferentialModelPersistence.Deserialize(json);
        Check(restored.ArtifactHash == fit.Artifact.ArtifactHash,
            "artifact hash survives round-trip");
        Check(restored.Predict(FixtureSamples(1)[0]).ExpectedVoltageV ==
            fit.Artifact.Predict(FixtureSamples(1)[0]).ExpectedVoltageV,
            "round-trip artifact replays the same expected voltage");

        var hash = fit.Artifact.ArtifactHash;
        var tampered = json.Replace(hash, new string('0', hash.Length), StringComparison.Ordinal);
        bool rejectedHash = false;
        try { _ = DifferentialModelPersistence.Deserialize(tampered); }
        catch (FormatException) { rejectedHash = true; }
        Check(rejectedHash, "tampered artifact hash is rejected");

        var oldSchema = json.Replace("\"schema_version\": 1", "\"schema_version\": 99",
            StringComparison.Ordinal);
        bool rejectedSchema = false;
        try { _ = DifferentialModelPersistence.Deserialize(oldSchema); }
        catch (NotSupportedException) { rejectedSchema = true; }
        Check(rejectedSchema, "unsupported artifact schema is not silently migrated");
    }

    static void Check(bool condition, string description)
    {
        if (!condition) throw new Exception("FAILED: " + description);
    }
}
