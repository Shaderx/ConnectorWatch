namespace ConnectorWatch;

public static class Tests
{
    public static void Run()
    {
        int passed = 0;
        void Check(bool ok, string name) { if (!ok) throw new Exception("FAILED: " + name); passed++; }
        const string fixtureUuid = "GPU-00000000-0000-0000-0000-000000000001";
        foreach (var setting in new string?[] { null, "", "  ", "auto", " AUTO " })
            Check(Nvml.ResolveUuid(setting, () => fixtureUuid) == fixtureUuid, "automatic GPU selection");
        Check(Nvml.ResolveUuid(" " + fixtureUuid + " ", () => throw new Exception("Unexpected enumeration")) == fixtureUuid, "explicit UUID bypasses auto-detection");
        Check(Nvml.SelectAutoUuid(1, () => fixtureUuid) == fixtureUuid, "single GPU auto-detected");
        foreach (uint count in new uint[] { 0, 2, 64 })
        {
            bool read = false, rejected = false;
            try { Nvml.SelectAutoUuid(count, () => { read = true; return fixtureUuid; }); } catch { rejected = true; }
            Check(rejected && !read, "ambiguous or missing GPU rejected before UUID read");
        }
        bool malformed = false;
        try { Nvml.SelectAutoUuid(1, () => "GPU-invalid"); } catch { malformed = true; }
        Check(malformed, "malformed detected UUID rejected");
        new Config().Validate();
        var c = new Config { StableSamples = 2, BaselineSamples = 5, WindowSamples = 3, SustainSamples = 2 };
        var a = new Analysis(c); var t = DateTimeOffset.UtcNow;
        Result Add(double w, double v) => a.Add(t = t.AddSeconds(1), w, v);
        Check(Add(440, 12).Status == "LOAD_SETTLING", "load settling");
        Check(a.Progress.StableSamples == 1 && a.Progress.StableTarget == 2 &&
            a.Progress.BaselineSamples == 5 && a.Progress.WindowTarget == 3,
            "analysis progress targets and settling count");
        for (int i = 0; i < 5; i++) Add(440, 12);
        var unverified = Add(440, 12);
        Check(unverified.Status == "REFERENCE_UNVERIFIED" && a.Bins[425].Reference is null &&
            a.Bins[425].CandidateReference == 12, "qualified candidate remains unverified");
        Check(a.Progress.WindowSamples == 3 && a.Progress.StableSamples == 7,
            "analysis progress rolling window and stable count");
        // The runtime lifecycle applies this mutation only after an explicit
        // operator acceptance. Keep the analysis fixture focused on proving
        // that accepted values, once supplied, remain frozen during detection.
        a.Bins[425].Reference = a.Bins[425].CandidateReference;
        a.Bins[425].ReferenceP05 = a.Bins[425].CandidateReferenceP05;
        a.Bins[425].CandidateReference = null;
        a.Bins[425].CandidateReferenceP05 = null;
        Check(Add(440, 11.7).Status == "SUDDEN_DROOP", "sudden droop");
        for (int i = 0; i < 5; i++) Add(440, 11.79);
        Check(Add(440, 11.79).Status == "BASELINE_SHIFT", "sustained median shift");
        Check(a.Bins[425].Reference == 12, "reference does not absorb degradation");
        var serialized = System.Text.Json.JsonSerializer.Serialize(a.Bins);
        var restored = System.Text.Json.JsonSerializer.Deserialize<Dictionary<int, Bin>>(serialized)!;
        var resumed = new Analysis(c, restored);
        resumed.Add(t.AddSeconds(1), 440, 11.7);
        Check(resumed.Add(t.AddSeconds(2), 440, 11.7).Status == "SUDDEN_DROOP", "saved reference survives restart");
        Check(Add(390, 11.7).Status == "LOAD_SETTLING", "new bin never compared to old load");
        a.Gap();
        Check(Add(30, 12).Status == "OUTSIDE_ANALYSIS_RANGE", "idle excluded");
        a.Gap(); Check(Add(440, 12).Status == "LOAD_SETTLING", "gap clears continuity");

        var early = new Analysis(new Config
        {
            StableSamples = 5, BaselineSamples = 5, WindowSamples = 3,
        });
        var earlyTime = DateTimeOffset.UtcNow;
        Check(early.Add(earlyTime, 440, 12).Status == "LOAD_SETTLING",
            "coarse guard observes during settling");
        Check(early.Add(earlyTime.AddSeconds(1), 440, 11.7).Status == "SUDDEN_DROOP",
            "abrupt drop is detected during settling");
        early.Gap();
        Check(early.Add(earlyTime.AddSeconds(2), null, 12).Status == "ANALYSIS_LOAD_UNAVAILABLE",
            "missing trend load is explicit");
        Check(early.Add(earlyTime.AddSeconds(3), null, 11.7).Status == "SUDDEN_DROOP",
            "abrupt drop is detected without trend load");

        var boundary = new Analysis(new Config
        {
            StableSamples = 3, BaselineSamples = 5, WindowSamples = 3,
            LoadBoundaryHysteresisWatts = 2.5,
        });
        var boundaryTime = DateTimeOffset.UtcNow;
        var boundaryStatuses = Enumerable.Range(0, 8).Select(i =>
            boundary.Add(boundaryTime.AddSeconds(i), i % 2 == 0 ? 449 : 451, 12).Status).ToArray();
        Check(boundaryStatuses.Skip(2).Any(status => status == "LEARNING_REFERENCE") &&
            boundaryStatuses.Skip(2).All(status => status != "LOAD_SETTLING"),
            "449/451 W oscillation does not suppress analysis indefinitely");
        Check(boundary.Add(boundaryTime.AddSeconds(8), 500, 12).Status == "LOAD_SETTLING",
            "material load transition restarts qualification");

        var invalidVoltage = new Analysis(new Config
        {
            StableSamples = 1, BaselineSamples = 5, WindowSamples = 3,
        });
        _ = invalidVoltage.Add(boundaryTime, 440, 12);
        Check(invalidVoltage.Add(boundaryTime.AddSeconds(1), 440, double.NaN).Status == "VOLTAGE_UNAVAILABLE",
            "invalid voltage cannot fabricate a coarse comparison");
        Check(invalidVoltage.Add(boundaryTime.AddSeconds(2), 440, 11.5).Status != "SUDDEN_DROOP",
            "first post-gap voltage is not compared with stale history");
        Check(Csv.Parse("a,\"b,c\",\"d\"\"e\"").SequenceEqual(new[] { "a", "b,c", "d\"e" }), "quoted CSV");
        var folder = Path.Combine(Path.GetTempPath(), "ConnectorWatch-test-" + Guid.NewGuid()); Directory.CreateDirectory(folder);
        try
        {
            var path = Path.Combine(folder, "sensors.csv");
            var stamp = DateTime.Now.AddSeconds(-1);
            c.HwinfoCsv = path; c.VoltageColumn = "GPU Input [V]"; c.PowerColumn = "GPU Power [W]";
            string header = "Date,Time,GPU Input [V],GPU Power [W]\r\n";
            string row = $"{stamp:d.M.yyyy},{stamp:H:mm:ss.fff},12.1,440\r\n";
            File.WriteAllText(path, header + row + "partial");
            var hw = new HwinfoLog(c);
            Check(hw.Read(DateTimeOffset.Now).Power == 440, "partial tail ignored and paired power read");
            bool stale = false; try { hw.Read(DateTimeOffset.Now.AddMinutes(1)); } catch (IOException) { stale = true; }
            Check(stale, "stale source rejected");
            File.WriteAllText(path, header + row.Replace("12.1", "0.9"));
            bool core = false; try { hw.Read(DateTimeOffset.Now); } catch (FormatException) { core = true; }
            Check(core, "core voltage rejected");
            File.WriteAllText(path, header.Replace("GPU Power [W]", "GPU Input [V]") + row);
            bool duplicate = false; try { hw.Read(DateTimeOffset.Now); } catch (FormatException) { duplicate = true; }
            Check(duplicate, "ambiguous columns rejected");
        }
        finally { Directory.Delete(folder, true); }
        var railFolder = Path.Combine(Path.GetTempPath(), "ConnectorWatch-rail-test-" + Guid.NewGuid()); Directory.CreateDirectory(railFolder);
        try
        {
            var railPath = Path.Combine(railFolder, "rail.jsonl");
            var railTime = DateTimeOffset.UtcNow.AddSeconds(-1);
            var record = System.Text.Json.JsonSerializer.Serialize(new
            {
                timestamp_utc = railTime,
                input_voltage_v = 12.08,
                power_w = 440.5,
                extra_voltages = new { pcie_12v = 12.01 }
            });
            File.WriteAllText(railPath, record + "\npartial");
            var rail = new RailJsonLog(new Config { GpuUuid = "test", RailJson = railPath });
            var sample = rail.Read(DateTimeOffset.UtcNow);
            Check(sample.Volts == 12.08 && sample.Power == 440.5, "timestamped JSON rail input");
            Check(sample.Extras.Contains("pcie_12v"), "timestamped JSON extra rail");
            Check(rail.Description == "timestamped rail JSON", "timestamped JSON source identity");
        }
        finally { Directory.Delete(railFolder, true); }
        bool nonFiniteConfig = false;
        try { new Config { GpuUuid = "test", SampleSeconds = double.NaN }.Validate(); }
        catch (Exception) { nonFiniteConfig = true; }
        Check(nonFiniteConfig, "non-finite configuration rejected");
        Check(DesktopAlerts.ShouldNotify("SUDDEN_DROOP") && DesktopAlerts.ShouldNotify("BASELINE_SHIFT") &&
            DesktopAlerts.ShouldNotify("VOLTAGE_UNAVAILABLE") && !DesktopAlerts.ShouldNotify("NO_SHIFT_DETECTED"),
            "desktop alert status filter");
        ControlServerTests.Run();
        HybridStorageTests.Run();
        DirectNvRailsTests.Run();
        ElectricalSamplesTests.Run();
        RailJsonTailTests.Run();
        CoarseRailGuardTests.Run();
        LoadQualifierTests.Run();
        TelemetryPhaseOneTests.Run();
        ReferenceLifecycleTests.Run();
        OfflineReplayTests.Run();
        Console.WriteLine($"PASS: {passed} behavioral checks.");
    }
}
