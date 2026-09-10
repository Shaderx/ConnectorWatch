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
        ElectricalTrendTests.Run(Check);
        DegradationConfidenceTests.Run(Check);
        ConfidenceHistoryTests.Run(Check);
        var now = DateTimeOffset.UtcNow;
        var approvedDriver = Snapshot.Parse("""
            {"driver_approval":{"state":"Revoked","detail":"Fixture revoked","driver_version":"999.99","catalog_revision":12,"unvalidated":false,"last_checked_utc":"2026-09-10T00:00:00Z"}}
            """);
        Check(approvedDriver.ApprovalState == "Revoked" && approvedDriver.CatalogRevision == 12 &&
            approvedDriver.DriverVersion == "999.99" && approvedDriver.ApprovalCheckedUtc.HasValue,
            "Driver approval diagnostics survive daemon-to-dashboard parsing");
        approvedDriver.Status = "DRIVER_AWAITING_APPROVAL";
        approvedDriver.ApprovalDetail = "This driver is awaiting maintainer approval.";
        Check(MainWindow.StatusMessage(approvedDriver, true, 100).Contains("awaiting maintainer approval") &&
            MainWindow.StatusMessage(approvedDriver, true, 100).Contains("Public GPU telemetry remains available"),
            "Unknown approval is explained without labeling the driver defective");
        var collecting = Snapshot.Parse("""
            {"electrical":{"Connector":{"Voltage":{"Value":12.08},"Power":{"Value":34.5}},"Freshness":{"IsFresh":true,"Kind":"HostPollTimestampUnverified"}},
             "analysis_load":{"Value":34.5,"IsAvailable":true,"Freshness":{"IsFresh":true,"Kind":"HostPollTimestampUnverified"}},
             "acquisition":{"status":"SENSOR_UNCHARACTERIZED"},"differential_model":{"state":null,"is_loaded":false}}
            """);
        Check(MainWindow.QualitySummary(collecting).Length == 0,
            "Fresh telemetry with unverified source timing and no learned model is not labeled degraded: " + MainWindow.QualitySummary(collecting));
        collecting.Status = "OUTSIDE_ANALYSIS_RANGE";
        collecting.ReferenceCompatibility = "LEGACY";
        var collectingMessage = MainWindow.StatusMessage(collecting, true, 100);
        Check(collectingMessage.StartsWith("Receiving telemetry.") && collectingMessage.Contains("timing is unverified") &&
            collectingMessage.Contains("no model loaded") && collectingMessage.Contains("accepted compatible reference") && collectingMessage.Contains("above 100 W"),
            "Live banner explains timing, model and reference limitations without implying missing samples");
        collecting.ResidualDetectorAlert = true; collecting.ResidualDetectorStatus = "SUDDEN_DROOP";
        Check(MainWindow.StatusMessage(collecting, true, 100, "Settings saved", "read failed").StartsWith("Sudden voltage drop"),
            "Residual alert takes priority over readiness, operation and read messages");
        Check(MainWindow.StatusMessage(collecting, false, 100).StartsWith("Monitoring unavailable"), "Historical alerts cannot imply live telemetry");
        collecting.ResidualDetectorAlert = false; collecting.IncidentCount = 12; collecting.ActiveIncidentCount = 0;
        Check(MainWindow.QualitySummary(collecting).Length == 0, "Resolved incident history does not degrade live telemetry");
        collecting.ActiveIncidentCount = 1;
        Check(MainWindow.StatusMessage(collecting, true, 100).StartsWith("1 active incident"), "Active incidents remain prominent");
        collecting.ActiveIncidentCount = 0; collecting.ElectricalStatus = "STALE";
        Check(MainWindow.StatusMessage(collecting, true, 100).StartsWith("Telemetry degraded: electrical stale"), "Stale electrical measurements still report degraded telemetry");
        collecting.Status = "BASELINE_SHIFT";
        Check(MainWindow.StatusMessage(collecting, true, 100).StartsWith("Sustained voltage shift"), "Trend alerts remain prominent even with degraded measurements");
        collecting.Status = "NO_SHIFT_DETECTED"; collecting.ElectricalStatus = "AVAILABLE"; collecting.AnalysisLoadStatus = "AVAILABLE";
        collecting.AcquisitionStatus = "HEALTHY"; collecting.ReferenceCompatibility = "COMPATIBLE";
        collecting.DifferentialModelFitted = true; collecting.DifferentialModelState = "FITTED";
        Check(!MainWindow.StatusMessage(collecting, true, 100).Contains("unavailable"), "Fitted healthy model does not show a missing-model warning");
        collecting.AcquisitionStatus = "SOURCE_ERROR";
        Check(MainWindow.StatusMessage(collecting, true, 100).StartsWith("Telemetry degraded:"), "Acquisition faults remain degraded");
        var logDirectory = Path.Combine(Path.GetTempPath(), "ConnectorWatch-log-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            string logPath = Path.Combine(logDirectory, "gui.jsonl");
            var log = new GuiLog(logPath);
            log.Write("telemetry_lost", new { sample_age_seconds = 9 }, new IOException("fixture error"), throttle: true);
            log.Write("telemetry_lost", throttle: true);
            var lines = File.ReadAllLines(logPath);
            Check(lines.Length == 1, "Repeated diagnostic errors are throttled");
            using var entry = JsonDocument.Parse(lines[0]);
            Check(entry.RootElement.GetProperty("details").GetProperty("sample_age_seconds").GetInt32() == 9 &&
                entry.RootElement.GetProperty("exception").GetString()!.Contains("fixture error"), "Diagnostic context and exceptions persist as JSON");
            var rotating = new GuiLog(logPath, 1);
            rotating.Write("telemetry_recovered");
            Check(File.Exists(logPath + ".1") && File.ReadAllLines(logPath).Length == 1, "Diagnostic log rotates");
            rotating.Write("next_event");
            Check(File.ReadAllText(logPath + ".1").Contains("telemetry_recovered"), "Rotation replaces the previous backup");
            new GuiLog(Path.Combine(logPath, "invalid.jsonl")).Write("unwritable_log");
            Check(true, "Logging failure does not escape into GUI");
        }
        finally { if (Directory.Exists(logDirectory)) Directory.Delete(logDirectory, true); }
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
        var lifecycleSnapshot = Snapshot.Parse("{\"reference_lifecycle\":{\"state\":\"REFERENCE_UNVERIFIED\",\"compatibility\":\"LEGACY\"}}");
        Check(lifecycleSnapshot.ReferenceState == "REFERENCE_UNVERIFIED" &&
            lifecycleSnapshot.ReferenceCompatibility == "LEGACY",
            "Reference lifecycle state remains visible to the GUI");
        var powerPathSnapshot = Snapshot.Parse($$"""
        {
          "timestamp_utc":"{{now:O}}",
          "voltage_source":"legacy path",
          "electrical":{
            "Source":"typed source path",
            "Connector":{
              "Voltage":{"Value":12.1,"Availability":"Present","Unit":"V"},
              "Current":{"Value":36.5,"Availability":"Present","Unit":"A"},
              "Power":{"Value":441.65,"Availability":"Present","Unit":"W"}
            },
            "Pcie":{
              "Voltage":{"Value":12.0,"Availability":"Present","Unit":"V"},
              "Current":{"Value":4.1,"Availability":"Present","Unit":"A"},
              "Power":{"Value":49.2,"Availability":"Present","Unit":"W"}
            },
            "Freshness":{"IsFresh":true,"Kind":"HostPollTimestampUnverified","Detail":"poll time only"}
          },
          "analysis_load":{"Source":"CONNECTOR_POWER","Value":441.65,"Unit":"W","IsAvailable":true,"Freshness":{"IsFresh":false,"Kind":"VerifiedSourceTimestamp"},"Detail":"stale selected load"},
          "reference_lifecycle":{"state":"REFERENCE_UNVERIFIED","compatibility":"COMPATIBLE"},
          "differential_model":{"is_loaded":true,"is_fitted":true,"state":"FITTED","apparent_slope_v_per_unit":-0.0002,"learning_samples":12,"detail":"fitted"},
          "differential_prediction":{"is_available":true,"expected_voltage_v":12.0,"observed_voltage_v":11.8,"residual_v":-0.2,"excess_droop_v":-0.19,"apparent_slope_v_per_unit":-0.0002,"qualification":"QUALIFIED","detail":"prediction"},
          "residual_detector":{"detector":"ewma","status":"SUDDEN_DROOP","is_available":true,"is_alert":true,"filtered_residual_v":-0.18,"consecutive_samples":3,"detail":"alert"},
          "power_limit_watchdog":{
            "result":{"Status":"POWER_LIMIT_OK","BoardPowerStatus":"BOARD_POWER_AVAILABLE","IsReadOnly":true,"NoSafetyCertification":true,"LimitAvailable":true,"BoardPowerAvailable":true,"IsFresh":true,"FreshnessVerified":false,"ConfiguredLimitWatts":450,"ObservedLimitWatts":450,"BoardPowerWatts":440,"IncidentLatched":false,"Detail":"read-only"}
          },
          "incidents":[{"incident_id":"i1","detector":"residual","Status":"SUDDEN_DROOP","severity":"HIGH","state":"LATCHED","TriggeredAtUtc":"{{now.AddSeconds(-1):O}}","post_complete":false}]
        }
        """);
        Check(powerPathSnapshot.ElectricalSourcePath == "typed source path" && powerPathSnapshot.ConnectorVoltage == 12.1 && powerPathSnapshot.ConnectorCurrent == 36.5 && powerPathSnapshot.ConnectorPower == 441.65 && powerPathSnapshot.PciePower == 49.2, "Typed connector and PCIe V/A/W path is decoded");
        Check(powerPathSnapshot.ElectricalStatus == "UNVERIFIED" && !powerPathSnapshot.ElectricalFreshnessVerified && powerPathSnapshot.AnalysisLoadStatus == "STALE", "Host-poll unverified and stale source states remain explicit");
        Check(powerPathSnapshot.AnalysisLoadSource == "CONNECTOR_POWER" && powerPathSnapshot.DifferentialModelFitted && powerPathSnapshot.DifferentialModelSlope == -0.0002 && powerPathSnapshot.ExpectedVoltage == 12.0 && powerPathSnapshot.Residual == -0.2, "Analysis load and differential prediction fields are decoded");
        Check(powerPathSnapshot.ResidualDetectorAlert && powerPathSnapshot.ResidualDetectorStatus == "SUDDEN_DROOP", "Residual detector state is decoded");
        Check(powerPathSnapshot.WatchdogAvailable && powerPathSnapshot.WatchdogReadOnly && powerPathSnapshot.WatchdogNoSafetyCertification && powerPathSnapshot.WatchdogBoardPower == 440, "Power-limit watchdog remains read-only in the GUI model");
        Check(powerPathSnapshot.IncidentCount == 1 && powerPathSnapshot.ActiveIncidentCount == 1 && powerPathSnapshot.LatestIncidentState == "LATCHED", "Incident count and lifecycle state are decoded");
        var unavailableLoad = Snapshot.Parse($"{{\"timestamp_utc\":\"{now:O}\",\"analysis_load\":{{\"Source\":\"CONNECTOR_POWER\",\"IsAvailable\":false,\"Freshness\":{{\"IsFresh\":false,\"Kind\":\"VerifiedSourceTimestamp\"}}}}}}");
        Check(unavailableLoad.AnalysisLoadStatus == "STALE" && unavailableLoad.AnalysisLoadValue == null, "Unavailable selected load stays blank and explicitly stale");
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
