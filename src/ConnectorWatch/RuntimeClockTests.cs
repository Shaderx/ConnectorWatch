using System.Text.Json;

namespace ConnectorWatch;

public static class RuntimeClockTests
{
    public static void Run()
    {
        var last = DateTimeOffset.Parse("2026-10-04T14:50:47.4642447Z");
        var corrected = DateTimeOffset.Parse("2026-10-04T14:50:46.6375385Z");
        RunSession([last, corrected, corrected.AddMilliseconds(200), corrected.AddMilliseconds(400)],
            rollback: true);
        RunSession([last, last.AddSeconds(1), last.AddSeconds(2)], rollback: false);

        var tracker = new AnalysisCoverageTracker(TimeSpan.FromMinutes(30));
        tracker.Record(CoverageObservation.AnalyzedAt(last));
        bool rejected = false;
        try { tracker.Record(CoverageObservation.UnknownLoadAt(corrected)); }
        catch (ArgumentException) { rejected = true; }
        Check(rejected, "coverage still rejects an unannounced backward observation");
        Console.WriteLine("PASS: runtime UTC rollback continuity, progress, freshness and reference preservation checks.");
    }

    static void RunSession(DateTimeOffset[] timestamps, bool rollback)
    {
        string folder = Path.Combine(Path.GetTempPath(), "ConnectorWatch-runtime-clock-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            string data = Path.Combine(folder, "data");
            Directory.CreateDirectory(data);
            var source = new ClockSource(rollback ? timestamps[0] : null);
            var config = new Config
            {
                GpuUuid = "GPU-00000000-0000-0000-0000-000000000001",
                DataDirectory = data,
                VoltageSource = "json",
                RailJson = Path.Combine(folder, "fixture.jsonl"),
                AnalysisLoadSource = AnalysisLoadSource.EXTERNAL_SENSOR_POWER.WireName(),
                DesktopAlerts = false,
                AutoAcceptReference = false,
                StableSamples = 1,
                BaselineSamples = 5,
                WindowSamples = 3,
            };
            string configPath = Path.Combine(folder, "config.json");
            File.WriteAllText(configPath, JsonSerializer.Serialize(config));
            string referencePath = Path.Combine(data, "reference.json");
            var reference = SeedReference(config, source.Description, timestamps[0]);
            File.WriteAllText(referencePath, ReferencePersistence.Serialize(reference));
            string acceptedBefore = JsonSerializer.Serialize(reference.Accepted);
            string archivedBefore = JsonSerializer.Serialize(reference.Archived);

            long tick = checked((long)MonotonicTime.Frequency);
            long monotonic = tick;
            int index = 0;
            var states = new List<JsonElement>();
            source.PollCompleted = () => monotonic += tick / 10;
            int exit = Program.RunSession(["--config", configPath, "--samples", timestamps.Length.ToString()],
                new RuntimeSessionOverrides
                {
                    UtcNow = () => timestamps[index],
                    MonotonicTimestamp = () => monotonic,
                    ReadGpu = () => { monotonic += tick; return new Gpu(440, 60, 95, 450); },
                    VoltageSource = source,
                    SkipSampleWait = true,
                    SkipBackgroundMaintenance = true,
                    DiagnosticLogDirectory = Path.Combine(folder, "logs"),
                    SampleCompleted = (number, json) =>
                    {
                        using var document = JsonDocument.Parse(json);
                        states.Add(document.RootElement.Clone());
                        index = Math.Min(number, timestamps.Length - 1);
                    },
                });

            Check(exit == 0 && states.Count == timestamps.Length,
                "production sampling session completes all polls after UTC correction");
            long previousCompleted = 0;
            for (int i = 0; i < states.Count; i++)
            {
                var state = states[i];
                var progress = state.GetProperty("sampling_progress");
                var coverage = state.GetProperty("coverage");
                Check(state.GetProperty("timestamp_utc").GetDateTimeOffset() == timestamps[i] &&
                    progress.GetProperty("last_completed_sample_utc").GetDateTimeOffset() == timestamps[i],
                    "published UTC timestamps retain the actual corrected clock");
                long completed = progress.GetProperty("last_completed_sample_monotonic").GetInt64();
                Check(completed > previousCompleted && progress.GetProperty("monitor_running").GetBoolean() &&
                    !state.GetProperty("stopped").GetBoolean() &&
                    progress.GetProperty("sample_age_seconds").GetDouble() == 0,
                    "sampling progress advances monotonically while the session remains active");
                previousCompleted = completed;
                Check(Math.Abs(state.GetProperty("poll_timing").GetProperty("latency_seconds").GetDouble() - 0.1) < 1e-9,
                    "poll latency uses monotonic time across UTC correction");
                Check(coverage.GetProperty("loaded_count").GetInt64() == (rollback && i > 0 ? i : i + 1),
                    "coverage starts a new interval once and admits polls before the old timestamp");
                if (rollback && i > 0)
                {
                    Check(state.GetProperty("detail").GetString()!.Contains("Host UTC clock moved backwards", StringComparison.Ordinal) == (i == 1),
                        "published detail identifies only the discontinuity poll");
                    Check(coverage.GetProperty("last_reset_reason").GetString() == "GENUINE_GAP" &&
                        coverage.GetProperty("last_reset_at_utc").GetDateTimeOffset() == timestamps[1] &&
                        coverage.GetProperty("horizon_start_utc").GetDateTimeOffset() == timestamps[1],
                        "coverage publishes a single explicit boundary at the corrected UTC timestamp");
                    Check(Math.Abs(coverage.GetProperty("loaded_duration_seconds").GetDouble() - (i - 1) * 1.1) < 1e-9,
                        "corrected coverage excludes the elapsed interval across the reset boundary");
                    var acquisition = state.GetProperty("acquisition");
                    Check(!acquisition.GetProperty("fresh").GetBoolean() &&
                        !acquisition.GetProperty("timestamp_valid").GetBoolean() &&
                        !acquisition.GetProperty("analysis_available").GetBoolean(),
                        "future source time remains unavailable while sampling continues");
                }
                else
                {
                    Check(coverage.GetProperty("last_reset_reason").GetString() == "STARTUP" &&
                        coverage.GetProperty("last_reset_at_utc").ValueKind == JsonValueKind.Null,
                        "advancing UTC preserves ordinary coverage without gap resets");
                }
            }

            var persisted = ReferencePersistence.Deserialize(File.ReadAllText(referencePath));
            Check(JsonSerializer.Serialize(persisted.Accepted) == acceptedBefore &&
                JsonSerializer.Serialize(persisted.Archived) == archivedBefore,
                "UTC correction preserves accepted reference values and archived history");
            Check(!rollback || persisted.State == ReferenceLifecycleState.REFERENCE_STALE,
                "source degradation keeps the existing stale-reference gate");
            var rows = File.ReadAllLines(Path.Combine(data, $"telemetry-{timestamps[0]:yyyy-MM-dd}.csv"));
            Check(rows.Length == timestamps.Length + 1,
                "all observations remain persisted after the coverage boundary");
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    static ReferencePersistenceDocument SeedReference(Config config, string provider, DateTimeOffset atUtc)
    {
        var identity = Program.BuildReferenceIdentity(config, "json", provider,
            AnalysisLoadSource.EXTERNAL_SENSOR_POWER, "external-source");
        var lifecycle = new ReferenceLifecycle(identity, atUtc.AddMinutes(-10));
        var candidate = ReferenceCandidateModel.Learned(identity,
            new Dictionary<int, ReferenceBinStatistics> { [425] = new(425, 12.1, 12.0, observedSamples: 5) },
            5, 5, atUtc.AddMinutes(-10), atUtc.AddMinutes(-9), true);
        lifecycle.SetCandidate(candidate);
        lifecycle.AcceptCandidate(atUtc.AddMinutes(-8), "fixture");
        lifecycle.ArchiveAccepted(atUtc.AddMinutes(-7), "fixture");
        lifecycle.SetCandidate(candidate);
        lifecycle.AcceptCandidate(atUtc.AddMinutes(-6), "fixture");
        return lifecycle.ToDocument();
    }

    sealed class ClockSource(DateTimeOffset? fixedTimestamp) : IVoltageSource
    {
        public string Description => "runtime clock fixture";
        internal Action? PollCompleted { get; set; }
        public Voltage Read(DateTimeOffset now)
        {
            PollCompleted?.Invoke();
            return new(fixedTimestamp ?? now, 12.1, 440, "{}");
        }
    }

    static void Check(bool condition, string name)
    {
        if (!condition) throw new Exception("FAILED: " + name);
    }
}
