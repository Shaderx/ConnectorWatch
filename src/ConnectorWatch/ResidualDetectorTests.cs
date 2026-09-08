namespace ConnectorWatch;

/// <summary>
/// Focused, hardware-free checks for issue #11.  The parent test harness can
/// invoke <see cref="Run"/> once the detector is wired into Analysis.
/// </summary>
public static class ResidualDetectorTests
{
    public static void Run()
    {
        FastDetectorFlagsOneSampleDroop();
        FastDetectorFailsClosedOnQualityGaps();
        EwmaFiltersNoiseAndRequiresPersistence();
        EwmaRecoversWithHysteresis();
        EwmaResetsAfterGap();
        IdentityIsAnExplicitGate();
        PredictionAdapterPreservesQualification();
        CompositeAndFactoryShareOneSeam();
        OptionsRejectUnsafeNumerics();
    }

    static void FastDetectorFlagsOneSampleDroop()
    {
        var detector = new FastResidualDetector(new ResidualDetectorOptions
        {
            FastDroopThresholdVolts = .25,
            MaximumGapSeconds = 10,
        });
        var t = At(0);
        var normal = detector.Update(ResidualObservation.Available(t, -.1));
        Check(normal.Status == ResidualDetectorStatus.NO_SHIFT_DETECTED &&
            !normal.IsAlert && normal.ResidualVolts == -.1,
            "fast detector passes normal residual");
        var droop = detector.Update(ResidualObservation.Available(At(1), -.3));
        Check(droop.Status == ResidualDetectorStatus.SUDDEN_DROOP &&
            droop.IsFastAlert && droop.IsAlert && droop.ExcessDroopVolts is > 0,
            "fast detector flags one-sample droop");
    }

    static void FastDetectorFailsClosedOnQualityGaps()
    {
        var detector = new FastResidualDetector(new ResidualDetectorOptions
        {
            MaximumGapSeconds = 2,
        });
        _ = detector.Update(ResidualObservation.Available(At(0), -.4));
        var gap = detector.Update(ResidualObservation.Available(At(5), -.4));
        Check(gap.Status == ResidualDetectorStatus.RESIDUAL_GAP &&
            !gap.IsAvailable && !gap.IsAlert && gap.StateReset,
            "fast detector reports timing gap");
        var missing = detector.Update(ResidualObservation.Unavailable(At(6), "source gap"));
        Check(missing.Status == ResidualDetectorStatus.RESIDUAL_UNAVAILABLE &&
            !missing.IsAlert && missing.StateReset,
            "fast detector fails closed on unavailable residual");
    }

    static void EwmaFiltersNoiseAndRequiresPersistence()
    {
        var detector = new EwmaResidualDetector(new ResidualDetectorOptions
        {
            EwmaAlpha = .5,
            EwmaDroopThresholdVolts = .2,
            EwmaRecoveryThresholdVolts = .05,
            EwmaConfirmationSamples = 3,
            MaximumGapSeconds = 10,
        });
        var t = At(0);
        var noise = new[] { -.10, -.12, -.08, -.11 }.Select((v, i) =>
            detector.Update(ResidualObservation.Available(t.AddSeconds(i), v))).ToArray();
        Check(noise.All(x => !x.IsAlert) && detector.Ewma is double ewma && ewma > -.2,
            "EWMA suppresses bounded noise");
        var first = detector.Update(ResidualObservation.Available(At(10), -.3));
        var second = detector.Update(ResidualObservation.Available(At(11), -.3));
        var third = detector.Update(ResidualObservation.Available(At(12), -.3));
        Check(!first.IsAlert && !second.IsAlert && third.IsAlert &&
            third.IsEwmaAlert && third.Status == ResidualDetectorStatus.BASELINE_SHIFT &&
            third.FilteredResidualVolts is < -.2,
            "EWMA requires configured persistence before alert");
    }

    static void EwmaRecoversWithHysteresis()
    {
        var detector = new EwmaResidualDetector(new ResidualDetectorOptions
        {
            EwmaAlpha = 1,
            EwmaDroopThresholdVolts = .2,
            EwmaRecoveryThresholdVolts = .05,
            EwmaConfirmationSamples = 1,
            MaximumGapSeconds = 10,
        });
        Check(detector.Update(ResidualObservation.Available(At(0), -.4)).IsAlert,
            "EWMA enters alert");
        var stillAlert = detector.Update(ResidualObservation.Available(At(1), -.08));
        Check(stillAlert.IsAlert && stillAlert.FilteredResidualVolts is double filtered &&
            Math.Abs(filtered - -.08) < 1e-12,
            "EWMA remains latched until recovery band");
        var recovered = detector.Update(ResidualObservation.Available(At(2), 0));
        Check(!recovered.IsAlert && recovered.Status == ResidualDetectorStatus.NO_SHIFT_DETECTED,
            "EWMA recovers outside hysteresis band");
    }

    static void EwmaResetsAfterGap()
    {
        var detector = new EwmaResidualDetector(new ResidualDetectorOptions
        {
            EwmaAlpha = 1,
            EwmaDroopThresholdVolts = .2,
            EwmaConfirmationSamples = 2,
            MaximumGapSeconds = 2,
        });
        _ = detector.Update(ResidualObservation.Available(At(0), -.4));
        var gap = detector.Update(ResidualObservation.Available(At(5), -.4));
        Check(gap.Status == ResidualDetectorStatus.RESIDUAL_GAP &&
            detector.Ewma is null && !gap.IsAlert,
            "EWMA discards state across source gap");
        var first = detector.Update(ResidualObservation.Available(At(6), -.4));
        Check(!first.IsAlert && first.ConsecutiveSamples == 1,
            "EWMA starts persistence after gap");
    }

    static void IdentityIsAnExplicitGate()
    {
        var detector = new FastResidualDetector(new ResidualDetectorOptions
        {
            ExpectedIdentity = "model-a",
        });
        var mismatch = detector.Update(ResidualObservation.Available(At(0), -.5, "model-b"));
        Check(mismatch.Status == ResidualDetectorStatus.RESIDUAL_IDENTITY_MISMATCH &&
            !mismatch.IsAlert && !mismatch.IsAvailable,
            "detector rejects identity mismatch");
        var accepted = detector.Update(ResidualObservation.Available(At(1), -.5, "model-a"));
        Check(accepted.IsAlert && accepted.Status == ResidualDetectorStatus.SUDDEN_DROOP,
            "detector accepts matching identity");
    }

    static void PredictionAdapterPreservesQualification()
    {
        var qualification = new DifferentialSampleQualification(
            false, false, true, true, true, true, true,
            new[] { DifferentialSampleRejectionReason.NOT_FRESH }, "stale");
        var prediction = new DifferentialPrediction(
            At(0), false, qualification, null, 11.5, null, null, null,
            "prediction unavailable");
        var observation = ResidualObservation.FromPrediction(prediction);
        Check(!observation.IsAvailable && !observation.IsFresh &&
            observation.ObservedVoltageV == 11.5 &&
            observation.UnavailableReason == "prediction unavailable",
            "prediction adapter preserves availability and quality");
        var result = new EwmaResidualDetector().Update(prediction);
        Check(!result.IsAvailable && !result.IsAlert,
            "prediction unavailable cannot become detector alert");
    }

    static void CompositeAndFactoryShareOneSeam()
    {
        IResidualDetector fast = ResidualDetectorFactory.Create(ResidualDetectorKind.FAST);
        IResidualDetector ewma = ResidualDetectorFactory.Create(ResidualDetectorKind.EWMA,
            new ResidualDetectorOptions { EwmaConfirmationSamples = 1 });
        var composite = new CompositeResidualDetector(fast, ewma);
        var result = composite.Update(ResidualObservation.Available(At(0), -.4));
        Check(result.Fast.IsFastAlert && result.Sustained.IsEwmaAlert && result.IsAlert &&
            result.Selected == result.Fast && result.Status == "SUDDEN_DROOP",
            "composite exposes swappable fast and sustained strategies");
        Check(ResidualDetectorFactory.CreateComposite() is CompositeResidualDetector,
            "factory creates composite strategy");
    }

    static void OptionsRejectUnsafeNumerics()
    {
        bool alphaRejected = false;
        try { new ResidualDetectorOptions { EwmaAlpha = 0 }.Validate(); }
        catch (ArgumentOutOfRangeException) { alphaRejected = true; }
        bool thresholdRejected = false;
        try { new ResidualDetectorOptions { FastDroopThresholdVolts = double.NaN }.Validate(); }
        catch (ArgumentOutOfRangeException) { thresholdRejected = true; }
        Check(alphaRejected && thresholdRejected,
            "residual detector rejects non-finite or zero thresholds");
    }

    static DateTimeOffset At(int seconds) =>
        new(2026, 1, 1, 0, 0, seconds, TimeSpan.Zero);

    static void Check(bool condition, string name)
    {
        if (!condition) throw new Exception("FAILED: " + name);
    }
}
