using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace ConnectorWatch;

public static class FailedSessionTests
{
    public static void Run()
    {
        int passed = 0;
        void Check(bool condition, string name)
        {
            if (!condition) throw new Exception("FAILED: " + name);
            passed++;
        }

        using (var fixture = new SessionFixture())
        {
            string? sampledState = null;
            bool finalizedWhileLocked = false;
            var runtime = fixture.Runtime with
            {
                SkipSampleWait = false,
                SampleCompleted = (completed, state) =>
                {
                    if (completed < 6)
                    {
                        fixture.UtcNow = fixture.UtcNow.AddSeconds(1);
                        return;
                    }
                    using var checkpoint = fixture.ReadStatus();
                    Check(!checkpoint.RootElement.GetProperty("stopped").GetBoolean(),
                        "production session persists a running checkpoint before the injected failure");
                    sampledState = state;
                    fixture.UtcNow = fixture.UtcNow.AddMinutes(1);
                    throw new InvalidOperationException("injected monitoring failure");
                },
                BeforeStoppedStatusWrite = (path, state) =>
                {
                    finalizedWhileLocked = !CanAcquireMonitorLock(fixture.Data);
                    using var finalCheckpoint = JsonDocument.Parse(state);
                    Check(finalCheckpoint.RootElement.GetProperty("stopped").GetBoolean(),
                        "final checkpoint is stopped before lock release");
                },
            };
            int exit = fixture.Run(runtime);
            Check(exit != 0 && sampledState is not null, "failure follows a completed production sample");
            using var final = fixture.ReadStatus();
            var state = final.RootElement;
            Check(state.GetProperty("stopped").GetBoolean(), "unexpected failure persists stopped flag");
            Check(finalizedWhileLocked && CanAcquireMonitorLock(fixture.Data),
                "finalization holds exclusive lock and releases it afterwards");
            Check(state.GetProperty("stopped_at_utc").GetDateTimeOffset() == fixture.UtcNow,
                "failure records a separate stop timestamp");
            Check(state.GetProperty("stop_reason").GetString() == "failure" &&
                state.GetProperty("failure_reason").GetString()!.Contains("injected monitoring failure", StringComparison.Ordinal),
                "failure reason describes the monitoring error");
            using var sample = JsonDocument.Parse(sampledState!);
            Check(state.GetProperty("timestamp_utc").GetDateTimeOffset() ==
                sample.RootElement.GetProperty("timestamp_utc").GetDateTimeOffset(),
                "finalization preserves the last real measurement timestamp");
            var progress = state.GetProperty("sampling_progress");
            Check(!progress.GetProperty("monitor_running").GetBoolean() &&
                progress.GetProperty("state").GetString() == "STOPPED" &&
                progress.GetProperty("last_completed_sample_utc").GetDateTimeOffset() ==
                    sample.RootElement.GetProperty("timestamp_utc").GetDateTimeOffset(),
                "sampling progress stops without replacing the last completed sample");
            var acquisition = state.GetProperty("acquisition");
            Check(acquisition.GetProperty("status").GetString() == "SOURCE_UNAVAILABLE" &&
                !acquisition.GetProperty("monitor_running").GetBoolean() &&
                !acquisition.GetProperty("analysis_available").GetBoolean() &&
                !acquisition.GetProperty("fresh").GetBoolean(),
                "acquisition reports unavailable monitoring and freshness");
            Check(!acquisition.GetProperty("MonitorAvailable").GetBoolean() &&
                acquisition.GetProperty("StatusName").GetString() == "SOURCE_UNAVAILABLE" &&
                !progress.GetProperty("IsRunning").GetBoolean(),
                "derived acquisition and sampling aliases agree with the stopped state");
            Check(acquisition.GetProperty("detectors").EnumerateObject().All(detector =>
                !detector.Value.GetProperty("available").GetBoolean() &&
                detector.Value.GetProperty("reason").GetString() == "MONITOR_UNAVAILABLE"),
                "detectors stop with the monitor");
            Check(acquisition.GetProperty("detectors").EnumerateObject().All(detector =>
                !detector.Value.GetProperty("IsAvailable").GetBoolean() &&
                detector.Value.GetProperty("ReasonName").GetString() == "MONITOR_UNAVAILABLE"),
                "derived detector aliases agree with unavailable monitoring");
            Check(fixture.ReadDiagnostic().Contains("System.InvalidOperationException: injected monitoring failure", StringComparison.Ordinal),
                "original failure retains its complete diagnostic");

            string failedInstance = state.GetProperty("instance_id").GetString()!;
            Check(fixture.Run(fixture.Runtime) == 0, "successor session exits normally");
            using var successor = fixture.ReadStatus();
            Check(successor.RootElement.GetProperty("instance_id").GetString() != failedInstance &&
                successor.RootElement.GetProperty("stop_reason").GetString() == "sample_limit" &&
                !successor.RootElement.TryGetProperty("failure_reason", out _),
                "successor checkpoint belongs to the new session");
        }

        using (var fixture = new SessionFixture())
        {
            string? sampledState = null;
            bool attemptedWhileLocked = false;
            var runtime = fixture.Runtime with
            {
                SkipSampleWait = false,
                SampleCompleted = (completed, state) =>
                {
                    if (completed < 6)
                    {
                        fixture.UtcNow = fixture.UtcNow.AddSeconds(1);
                        return;
                    }
                    sampledState = state;
                    throw new InvalidOperationException("primary failure remains visible");
                },
                BeforeStoppedStatusWrite = (path, _) =>
                {
                    attemptedWhileLocked = !CanAcquireMonitorLock(fixture.Data);
                    Directory.CreateDirectory(path + ".tmp");
                },
            };
            using var errors = new StringWriter();
            var previousError = Console.Error;
            int exit;
            try { Console.SetError(errors); exit = fixture.Run(runtime); }
            finally { Console.SetError(previousError); }
            Check(exit != 0 && sampledState is not null && attemptedWhileLocked,
                "status-write failure keeps nonzero exit and lock ownership");
            Check(errors.ToString().Contains("could not mark stopped state", StringComparison.Ordinal) &&
                errors.ToString().Contains("primary failure remains visible", StringComparison.Ordinal),
                "status-write and monitoring failures remain visible");
            string diagnostic = fixture.ReadDiagnostic();
            Check(diagnostic.Contains("System.InvalidOperationException: primary failure remains visible", StringComparison.Ordinal) &&
                diagnostic.Contains("could not mark stopped state", StringComparison.Ordinal),
                "persistence failure does not hide or replace the original diagnostic");
        }

        using (var fixture = new SessionFixture())
        {
            const string ownerState = "{\"instance_id\":\"existing-owner\",\"timestamp_utc\":\"2026-10-04T14:50:47Z\",\"stopped\":false}";
            File.WriteAllText(fixture.StatusPath, ownerState);
            bool attemptedFinalization = false;
            using (var owner = new FileStream(Path.Combine(fixture.Data, "monitor.lock"),
                FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
            {
                Check(fixture.Run(fixture.Runtime with
                {
                    BeforeStoppedStatusWrite = (_, _) => attemptedFinalization = true,
                }) != 0, "pre-lock failure exits nonzero");
                Check(!attemptedFinalization && File.ReadAllText(fixture.StatusPath) == ownerState,
                    "pre-lock failure cannot overwrite the existing owner's checkpoint");
            }
            Check(fixture.Run(fixture.Runtime) == 0, "normal session follows released previous lock");
            using var normal = fixture.ReadStatus();
            Check(normal.RootElement.GetProperty("stopped").GetBoolean() &&
                normal.RootElement.GetProperty("stop_reason").GetString() == "sample_limit" &&
                normal.RootElement.GetProperty("instance_id").GetString() != "existing-owner",
                "normal shutdown keeps its result and its own checkpoint");
        }

        using (var fixture = new SessionFixture())
        {
            File.WriteAllText(fixture.StatusPath,
                "{\"instance_id\":\"previous-session\",\"timestamp_utc\":\"2026-10-04T14:50:47Z\",\"gpu\":{\"Power\":999},\"stopped\":false}");
            Check(fixture.Run(fixture.Runtime with
            {
                ReadGpu = () => throw new InvalidOperationException("failure before first sample"),
            }) != 0, "owned-lock failure before first sample exits nonzero");
            using var failed = fixture.ReadStatus();
            Check(failed.RootElement.GetProperty("stopped").GetBoolean() &&
                failed.RootElement.GetProperty("instance_id").GetString() != "previous-session" &&
                failed.RootElement.GetProperty("timestamp_utc").ValueKind == JsonValueKind.Null &&
                failed.RootElement.GetProperty("gpu").ValueKind == JsonValueKind.Null,
                "new session does not borrow the previous owner's measurement");
        }

        using (var fixture = new SessionFixture())
        {
            bool stopAccepted = false;
            Check(fixture.Run(fixture.Runtime with
            {
                SampleCompleted = (_, state) =>
                {
                    using var sample = JsonDocument.Parse(state);
                    stopAccepted = SendStop(fixture.Data,
                        sample.RootElement.GetProperty("instance_id").GetString()!);
                },
            }) == 0 && stopAccepted, "control shutdown retains successful result");
            using var stopped = fixture.ReadStatus();
            Check(stopped.RootElement.GetProperty("stop_reason").GetString() == "control" &&
                !stopped.RootElement.TryGetProperty("failure_reason", out _),
                "control shutdown retains its reason");
        }

        using (var fixture = new SessionFixture())
        {
            Check(fixture.Run(fixture.Runtime with
            {
                VoltageSource = new FixtureVoltageSource(driverChanged: true),
            }) == 42, "driver change retains the session restart result");
            using var stopped = fixture.ReadStatus();
            Check(stopped.RootElement.GetProperty("stopped").GetBoolean() &&
                stopped.RootElement.GetProperty("stop_reason").GetString() == "signal" &&
                !stopped.RootElement.TryGetProperty("failure_reason", out _),
                "cancellation shutdown retains its existing reason");
        }

        using (var fixture = new SessionFixture())
        {
            Check(fixture.Run(fixture.Runtime with
            {
                VoltageSource = new FixtureVoltageSource(terminalFailure: true),
            }) != 0, "terminal source failure retains nonzero exit");
            using var failed = fixture.ReadStatus();
            var state = failed.RootElement;
            var acquisition = state.GetProperty("acquisition");
            Check(state.GetProperty("stopped").GetBoolean() &&
                acquisition.GetProperty("status").GetString() == "NATIVE_FAILURE" &&
                acquisition.GetProperty("detail").GetString()!.Contains("injected terminal source failure", StringComparison.Ordinal),
                "stopped checkpoint preserves native failure classification and detail");
            Check(!acquisition.GetProperty("MonitorAvailable").GetBoolean() &&
                acquisition.GetProperty("StatusName").GetString() == "NATIVE_FAILURE",
                "derived acquisition aliases preserve native failure without reporting availability");
            Check(!acquisition.GetProperty("monitor_running").GetBoolean() &&
                !acquisition.GetProperty("fresh").GetBoolean() &&
                !acquisition.GetProperty("analysis_available").GetBoolean() &&
                state.GetProperty("sampling_progress").GetProperty("state").GetString() == "STOPPED",
                "native failure still stops progress and acquisition availability");
            Check(acquisition.GetProperty("detectors").EnumerateObject().All(detector =>
                !detector.Value.GetProperty("available").GetBoolean() &&
                detector.Value.GetProperty("reason").GetString() == "NATIVE_FAILURE"),
                "detectors retain the terminal native failure cause");
            Check(acquisition.GetProperty("detectors").EnumerateObject().All(detector =>
                !detector.Value.GetProperty("IsAvailable").GetBoolean() &&
                detector.Value.GetProperty("ReasonName").GetString() == "NATIVE_FAILURE"),
                "derived detector aliases preserve the unavailable native failure cause");
            Check(fixture.ReadDiagnostic().Contains("System.InvalidOperationException: injected terminal source failure", StringComparison.Ordinal),
                "terminal source failure preserves the original cause diagnostic");
        }

        Console.WriteLine($"PASS: {passed} failed-session runtime checks.");
    }

    static bool SendStop(string data, string instanceId)
    {
        using var client = new NamedPipeClientStream(".", ControlEndpoint.Name(data),
            PipeDirection.InOut, PipeOptions.CurrentUserOnly);
        client.Connect(2000);
        using var writer = new StreamWriter(client, new UTF8Encoding(false), 1024, leaveOpen: true)
        { AutoFlush = true };
        writer.WriteLine(ControlProtocol.Serialize(new ControlRequest("stop", "failed-session-test", instanceId)));
        using var reader = new StreamReader(client, Encoding.UTF8, false, 1024, leaveOpen: true);
        string? response = reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(2)).GetAwaiter().GetResult();
        return response is not null && JsonSerializer.Deserialize<ControlResponse>(response, ControlProtocol.Json)?.Ok == true;
    }

    static bool CanAcquireMonitorLock(string data)
    {
        try
        {
            using var candidate = new FileStream(Path.Combine(data, "monitor.lock"),
                FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            return true;
        }
        catch (IOException) { return false; }
    }

    sealed class SessionFixture : IDisposable
    {
        readonly string root = Path.Combine(Path.GetTempPath(), "ConnectorWatch-failed-session-" + Guid.NewGuid().ToString("N"));
        readonly string configPath;
        long monotonic = System.Diagnostics.Stopwatch.GetTimestamp();
        public SessionFixture()
        {
            Data = Path.Combine(root, "data");
            Directory.CreateDirectory(Data);
            configPath = Path.Combine(root, "config.json");
            File.WriteAllText(configPath, JsonSerializer.Serialize(new Config
            {
                GpuUuid = "GPU-00000000-0000-0000-0000-000000000021",
                DataDirectory = Data,
                VoltageSource = "none",
                AnalysisLoadSource = "NVML_BOARD_POWER",
                DesktopAlerts = false,
                AutoAcceptReference = false,
                StableSamples = 1,
                BaselineSamples = 5,
                WindowSamples = 3,
                SampleSeconds = .2,
                FlushSeconds = 1,
            }));
            Runtime = new RuntimeSessionOverrides
            {
                UtcNow = () => UtcNow,
                MonotonicTimestamp = () => monotonic += System.Diagnostics.Stopwatch.Frequency,
                ReadGpu = () => new Gpu(440, 55, 95, 500),
                VoltageSource = new FixtureVoltageSource(),
                SkipSampleWait = true,
                SkipBackgroundMaintenance = true,
                DiagnosticLogDirectory = Path.Combine(root, "logs"),
            };
        }
        public DateTimeOffset UtcNow { get; set; } = new(2026, 10, 4, 14, 50, 47, TimeSpan.Zero);
        public string Data { get; }
        public string StatusPath => Path.Combine(Data, "status.json");
        public RuntimeSessionOverrides Runtime { get; }
        public int Run(RuntimeSessionOverrides runtime) => Program.RunSession(["--config", configPath, "--samples", "6"], runtime);
        public JsonDocument ReadStatus() => JsonDocument.Parse(File.ReadAllText(StatusPath));
        public string ReadDiagnostic() => File.ReadAllText(Path.Combine(Runtime.DiagnosticLogDirectory!, "errors.log"));
        public void Dispose() => Directory.Delete(root, true);
    }

    sealed class FixtureVoltageSource(bool driverChanged = false, bool terminalFailure = false) : IVoltageSource
    {
        public string Description => "failed-session fixture voltage";
        public bool TerminalOnFailure => terminalFailure;
        public Voltage Read(DateTimeOffset now) => driverChanged
            ? throw new DriverSessionChangedException()
            : terminalFailure ? throw new InvalidOperationException("injected terminal source failure")
            : new(now, 12, 440, "{}");
    }
}
