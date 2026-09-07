using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace ConnectorWatch.Gui;

public static class GuiTests
{
    public static void Run(string? output)
    {
        var checks = new List<string>();
        void Check(bool ok, string name) { if (!ok) throw new Exception("FAIL: " + name); checks.Add(name); }
        var now = DateTimeOffset.UtcNow;
        LiveStorageChecks(Check);
        PointSample Point(int i, double v, int? bin = 425, string state = "NO_SHIFT_DETECTED") => new(now.AddSeconds(i), now.AddSeconds(i), v, 12.1, 440, bin, state, 12.1, 12.09, 12.1 - v, "");
        var samples = new[] { Point(-5, 12.1), Point(-4, 12.11), Point(-4, 12.11), Point(-3, 11.8, 450), Point(-2, 12, null, "OUTSIDE_ANALYSIS_RANGE"), Point(-1, 12.2, 425, "LOAD_SETTLING") };
        var selected = PlotData.Select(samples, now.AddMinutes(-1), now, 425, true);
        Check(selected.Count == 2, "Histogram excludes duplicate source timestamps, other loads and settling");
        var h = PlotData.Histogram(selected);
        Check(h.Total == 2 && h.Counts.Sum() == 2, "Histogram counts match raw eligible samples");
        Check(Math.Abs(h.Median!.Value - 12.105) < 1e-8 && Math.Abs(h.P05!.Value - 12.1005) < 1e-8, "Histogram interpolated statistics");
        Check(PlotData.Select(samples, now.AddMinutes(-1), now, null, false).Count == 5, "All-load view includes idle, still deduplicates");
        Check(PlotData.Histogram(Array.Empty<PointSample>()).Total == 0, "Empty histogram has no invented data");
        var dense = Enumerable.Range(-10000, 10000).Select(i => Point(i, i == -4567 ? 11.7 : 12.1)).ToArray();
        var reduced = PlotData.Downsample(dense, 100, now.AddSeconds(-10000), now);
        Check(reduced.Any(p => p.Voltage == 11.7) && reduced.Count < 1000, "History reduction preserves a single-sample dip");
        var stale = new Snapshot { Time = now.AddSeconds(-20), Voltage = 12.1 };
        Check(!stale.Fresh(), "Stale values cannot look live");
        Check(!new Snapshot { Time = now, Stopped = true }.Fresh(), "Stopped values cannot look live");
        Check(!new Snapshot { Time = now.AddMinutes(1) }.Fresh(), "Future timestamps are unavailable");
        Check(new Snapshot { Time = now }.Fresh(), "Recent running snapshot is live");
        Check(Snapshot.Parse("{\"gpu_uuid\":\"GPU-example\"}").GpuUuid == "GPU-example", "Detected UUID reaches GUI snapshot");
        Check(Snapshot.Parse("{}").GpuUuid == "", "Older snapshots without UUID remain readable");
        var csv = TelemetryStore.ParseCsv("one,\"two,three\",\"a\"\"b\"");
        Check(csv.SequenceEqual(new[] { "one", "two,three", "a\"b" }), "CSV quoting and escaped quotes");
        var bounded = new BoundedBuffer<int>(7);
        for (int i = 0; i < 10000; i++) bounded.Add(i);
        Check(bounded.Count == 7 && bounded.Capacity == 7 && bounded.SequenceEqual(Enumerable.Range(9993, 7)), "Ring history never exceeds its storage limit");
        bounded.RemoveAll(n => n % 2 == 0); bounded.Add(10000);
        Check(bounded.SequenceEqual(new[] { 9993, 9995, 9997, 9999, 10000 }), "Wrapped history compaction preserves chronological order");
        var ids = new BoundedIdSet(2000);
        for (int i = 0; i < 100000; i++) ids.Add("warning-" + i);
        Check(ids.Count == 2000 && !ids.Contains("warning-0") && ids.Contains("warning-99999"), "Warning identifiers evict oldest entries in memory");
        var rev = ids.Revision; ids.Add("warning-99999");
        Check(ids.Count == 2000 && ids.Revision == rev, "Repeated warning IDs do not consume retention slots");
        string temp = Path.Combine(Path.GetTempPath(), "ConnectorWatch-gui-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try
        {
            string path = Path.Combine(temp, "telemetry-" + now.ToString("yyyy-MM-dd") + ".csv");
            string header = "timestamp_utc,voltage_timestamp_utc,input_voltage_v,analysis_power_w,bin_w,status,extra_voltages_json\n";
            string line = $"{now:O},{now:O},12.1,440,425,NO_SHIFT_DETECTED,{{}}";
            File.WriteAllText(path, header + line);
            File.WriteAllText(Path.Combine(temp, "status.json"), JsonSerializer.Serialize(new { schema_version = 2, timestamp_utc = now, stopped = false, analysis = new { Status = "NO_SHIFT_DETECTED", Bin = 425 }, progress = new { learning_samples = 123, window_samples = 45 } }));
            var store = new TelemetryStore(temp); store.RefreshAsync().GetAwaiter().GetResult();
            Check(store.Samples.Count == 0, "Incomplete tail is not charted");
            File.AppendAllText(path, "\n"); store.RefreshAsync().GetAwaiter().GetResult();
            Check(store.Samples.Count == 1, "Completed tail is consumed once");
            store.RefreshAsync().GetAwaiter().GetResult();
            Check(store.Samples.Count == 1, "Incremental polling does not duplicate CSV rows");
            Check(store.Current?.Learning == 123 && store.Current.Window == 45, "Versioned progress snapshot decoded");
            // Reproduce the retention bypass through the real refresh path:
            // telemetry advances, but a locked event file throws before pruning.
            var oldPoint = Point(-90000, 12.1);
            for (int i = 0; i < 200_001; i++) store.Samples.Add(oldPoint);
            using (var blockedEvents = new FileStream(Path.Combine(temp, "events.csv"), FileMode.Create, FileAccess.ReadWrite, FileShare.None))
            {
                File.AppendAllText(path, line + "\n");
                store.RefreshAsync().GetAwaiter().GetResult();
                Check(store.Samples.Count <= 200_000 && store.Samples.All(p => p.Time >= now.AddHours(-24)), "History limits survive a locked events file");
            }
            File.WriteAllText(Path.Combine(temp, "oversized.json"), new string('x', 1025));
            bool rejected = false; try { TelemetryStore.ReadShared(Path.Combine(temp, "oversized.json"), 1024); } catch (InvalidDataException) { rejected = true; }
            Check(rejected, "Structured-file reads reject oversized payloads before full allocation");
            // Quote parity and UTF-8 decoder state span both chunks and polling turns.
            File.WriteAllText(path, header);
            var tails = new TelemetryStore(temp); tails.RefreshAsync().GetAwaiter().GetResult();
            var eventPath = Path.Combine(temp, "events.csv");
            File.WriteAllText(eventPath, $"{now:O},BASELINE_SHIFT,0.2,\"first line\nsecond \"\"quoted\"\" line");
            tails.RefreshAsync().GetAwaiter().GetResult();
            Check(tails.Incidents.Count == 0, "Multiline incomplete CSV record is retained without being emitted");
            File.AppendAllText(eventPath, "\"\n"); tails.RefreshAsync().GetAwaiter().GetResult();
            Check(tails.Incidents.Count == 1 && tails.Incidents[0].Detail == "first line\nsecond \"quoted\" line", "Multiline quoted CSV survives split polling reads");
            File.WriteAllText(eventPath, $"{now:O},BASELINE_SHIFT,0.2,\"" + new string('x', 300000) + "\n" + $"{now:O},BASELINE_SHIFT,0.2,recovered\n");
            var longRow = new TelemetryStore(temp); longRow.RefreshAsync().GetAwaiter().GetResult();
            Check(longRow.Limited && longRow.Incidents.Count == 1 && longRow.Incidents[0].Detail == "recovered", "Oversized unfinished rows are bounded and parser recovers");
        }
        finally { Directory.Delete(temp, true); }
        string result = JsonSerializer.Serialize(new { passed = checks.Count, checks }, new JsonSerializerOptions { WriteIndented = true });
        if (output != null) File.WriteAllText(output, result);
    }

    static void LiveStorageChecks(Action<bool, string> check)
    {
        string directory = Path.Combine(Path.GetTempPath(), "ConnectorWatch-live-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var now = DateTimeOffset.UtcNow;
        string header = "timestamp_utc,voltage_timestamp_utc,input_voltage_v,analysis_power_w,bin_w,status,extra_voltages_json\n";
        string row = string.Concat(Enumerable.Range(0, 200).Select(i => {
            var time = now.AddMilliseconds((i - 199) * 200);
            return $"{time:O},{time:O},12.1,440,425,NO_SHIFT_DETECTED,{{}}\n";
        }));
        string status = JsonSerializer.Serialize(new { timestamp_utc = now, stopped = false, voltage = new { Volts = 12.1 }, analysis = new { Status = "NO_SHIFT_DETECTED" } });
        using var ready = new System.Threading.ManualResetEventSlim();
        var server = System.Threading.Tasks.Task.Run(async () => {
            for (int i = 0; i < 4; i++)
            {
                using var pipe = new System.IO.Pipes.NamedPipeServerStream(ConnectorWatch.ControlEndpoint.Name(directory), System.IO.Pipes.PipeDirection.InOut, 1, System.IO.Pipes.PipeTransmissionMode.Byte, System.IO.Pipes.PipeOptions.Asynchronous | System.IO.Pipes.PipeOptions.CurrentUserOnly);
                ready.Set();
                using var timeout = new System.Threading.CancellationTokenSource(10000);
                await pipe.WaitForConnectionAsync(timeout.Token);
                using var reader = new StreamReader(pipe, System.Text.Encoding.UTF8, false, 1024, true);
                using var writer = new StreamWriter(pipe, new System.Text.UTF8Encoding(false), 1024, true) { AutoFlush = true };
                var request = JsonSerializer.Deserialize<ConnectorWatch.ControlRequest>((await reader.ReadLineAsync(timeout.Token))!, ConnectorWatch.ControlProtocol.Json)!;
                var response = new ConnectorWatch.ControlResponse(1, Environment.ProcessId, directory, "test-instance", true,
                    request.Command == "live" ? new ConnectorWatch.LiveTelemetry(status, header, row.Split('\n', StringSplitOptions.RemoveEmptyEntries)) : null);
                await writer.WriteLineAsync(ConnectorWatch.ControlProtocol.Serialize(response));
            }
        });
        try
        {
            ready.Wait();
            var store = new TelemetryStore(directory);
            store.RefreshAsync().GetAwaiter().GetResult();
            check(store.Current?.Fresh() == true && store.Samples.Count == 200 && Directory.GetFiles(directory).Length == 0, "Live pipe supplies cards and history above 16 KiB before any disk checkpoint");
            File.WriteAllText(Path.Combine(directory, "telemetry-" + now.ToString("yyyy-MM-dd") + ".csv"), header + row);
            File.WriteAllText(Path.Combine(directory, "status.json"), JsonSerializer.Serialize(new { timestamp_utc = now.AddMinutes(-5), stopped = false }));
            store.RefreshAsync().GetAwaiter().GetResult();
            check(store.Current?.Fresh() == true && store.Samples.Count == 200, "Live state overrides old checkpoint and flushed samples are not duplicated");
            server.GetAwaiter().GetResult();
            store.RefreshAsync().GetAwaiter().GetResult();
            check(store.Current?.Fresh() == false, "Disconnected live pipe cannot make old disk checkpoint look fresh");
        }
        finally { server.GetAwaiter().GetResult(); Directory.Delete(directory, true); }
    }
}
