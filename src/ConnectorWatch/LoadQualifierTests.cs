namespace ConnectorWatch;

/// <summary>Offline checks for continuous/hysteretic legacy load qualification.</summary>
public static class LoadQualifierTests
{
    public static void Run()
    {
        var start = new DateTimeOffset(2026, 9, 9, 0, 0, 0, TimeSpan.Zero);
        var options = new LoadQualificationOptions
        {
            MinimumLoadWatts = 100,
            MaximumLoadWatts = 1000,
            BinWatts = 25,
            BoundaryHysteresisWatts = 2.5,
            MinimumStableSamples = 3,
        };
        var qualifier = new HystereticLoadQualifier(options);

        var first = qualifier.Observe(start, 449, elapsedSeconds: 0);
        Check(first.State == LoadQualificationState.Settling && first.Bin == 425 &&
            first.Transition == LoadQualificationTransition.FirstObservation,
            "first 449 W observation starts the 425 W segment");
        var chatter = qualifier.Observe(start.AddSeconds(1), 451, elapsedSeconds: 1);
        Check(chatter.Bin == 425 && chatter.RawBin == 450 &&
            chatter.Transition == LoadQualificationTransition.HysteresisHold &&
            chatter.IsComparable,
            "449/451 W oscillation stays in one hysteretic segment");
        var qualified = qualifier.Observe(start.AddSeconds(2), 449,
            elapsedSeconds: 2);
        Check(qualified.IsQualified && qualified.Bin == 425 &&
            qualified.AnalysisLoadWatts.HasValue &&
            Math.Floor(qualified.AnalysisLoadWatts.Value / 25) * 25 == qualified.Bin,
            "held band qualifies and exposes a canonical legacy-analysis load");
        Check(Math.Abs(qualified.AnalysisLoadWatts!.Value -
            chatter.AnalysisLoadWatts!.Value) < .000001,
            "boundary chatter keeps the same canonical analysis load");

        var crossed = qualifier.Observe(start.AddSeconds(3), 453,
            elapsedSeconds: 3);
        Check(crossed.Transition == LoadQualificationTransition.BandChanged &&
            crossed.Bin == 450 && crossed.State == LoadQualificationState.Settling &&
            !crossed.IsComparable,
            "load beyond hysteresis starts a new settling band");

        // The reverse sequence also holds its initial band: the result does
        // not depend on which side of the boundary happened to be observed
        // first.
        var reverse = new HystereticLoadQualifier(options);
        var highFirst = reverse.Observe(start, 451, elapsedSeconds: 0);
        var highToLow = reverse.Observe(start.AddSeconds(1), 449,
            elapsedSeconds: 1);
        Check(highFirst.Bin == 450 && highToLow.Bin == 450 &&
            highToLow.Transition == LoadQualificationTransition.HysteresisHold &&
            highToLow.IsComparable,
            "reverse 451/449 oscillation stays in one hysteretic segment");

        // A large change starts a new segment even if a caller configured an
        // unusually wide bin; materially different loads are never compared.
        var wide = new HystereticLoadQualifier(new LoadQualificationOptions
        {
            BinWatts = 100,
            BoundaryHysteresisWatts = 5,
            MaximumComparableDeltaWatts = 10,
            MinimumStableSamples = 1,
        });
        _ = wide.Observe(start, 440, elapsedSeconds: 0);
        var material = wide.Observe(start.AddSeconds(1), 449,
            elapsedSeconds: 1);
        Check(material.Transition == LoadQualificationTransition.None &&
            material.IsComparable,
            "small same-band load movement remains comparable");
        var materialJump = wide.Observe(start.AddSeconds(2), 470,
            elapsedSeconds: 2);
        Check(materialJump.Transition == LoadQualificationTransition.BandChanged &&
            !materialJump.IsComparable,
            "material load movement restarts qualification");

        // Stable duration is elapsed-time-aware in addition to sample-count
        // stability, and uses the supplied monotonic poll clock.
        var timed = new HystereticLoadQualifier(new LoadQualificationOptions
        {
            MinimumStableSamples = 1,
            MinimumStableDuration = TimeSpan.FromSeconds(2),
        });
        Check(timed.Observe(start, 440, elapsedSeconds: 0).State ==
            LoadQualificationState.Settling,
            "duration-qualified load waits at first sample");
        Check(timed.Observe(start.AddSeconds(1), 440, elapsedSeconds: 1).State ==
            LoadQualificationState.Settling,
            "duration-qualified load waits before elapsed target");
        Check(timed.Observe(start.AddSeconds(2), 440, elapsedSeconds: 2).IsQualified,
            "duration-qualified load confirms after elapsed target");

        var gaps = new HystereticLoadQualifier(options);
        _ = gaps.Observe(start, 440, elapsedSeconds: 0);
        var stale = gaps.Observe(new LoadQualificationObservation(start.AddSeconds(1),
            441, IsFresh: false, ElapsedSeconds: 1));
        Check(stale.State == LoadQualificationState.Invalid && stale.IsGap &&
            !stale.IsComparable,
            "stale load resets continuity without comparison");
        var afterStale = gaps.Observe(start.AddSeconds(2), 441, elapsedSeconds: 2);
        Check(afterStale.State == LoadQualificationState.Gap && afterStale.IsGap &&
            !afterStale.IsComparable,
            "first post-stale load starts an explicit gap segment");

        var repeated = gaps.Observe(start.AddSeconds(2), 441, elapsedSeconds: 2);
        Check(repeated.State == LoadQualificationState.Gap && repeated.IsGap,
            "repeated timestamp cannot qualify a load comparison");
        var overAge = gaps.Observe(start.AddSeconds(10), 441, elapsedSeconds: 10);
        Check(overAge.State == LoadQualificationState.Gap && overAge.IsGap,
            "over-age load gap cannot qualify against prior data");

        var invalid = gaps.Observe(start.AddSeconds(11), double.NaN,
            elapsedSeconds: 11);
        Check(invalid.State == LoadQualificationState.Invalid && invalid.IsGap,
            "non-finite load is rejected");
        var outside = gaps.Observe(start.AddSeconds(12), 99, elapsedSeconds: 12);
        Check(outside.State == LoadQualificationState.Invalid && outside.IsGap,
            "below-minimum load is rejected");

        Console.WriteLine("PASS: continuous/hysteretic legacy load qualifier and gap handling.");
    }

    static void Check(bool condition, string name)
    {
        if (!condition) throw new Exception("FAILED: " + name);
    }
}

