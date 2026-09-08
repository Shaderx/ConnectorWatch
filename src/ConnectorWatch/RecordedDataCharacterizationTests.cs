using System.Text.Json;

namespace ConnectorWatch;

/// <summary>Offline characterization checks.  The fixture is intentionally
/// recorded (not live): it proves parsing and arithmetic without claiming a
/// physical sensor result.</summary>
public static class RecordedDataCharacterizationTests
{
    public static void Run()
    {
        int passed = 0;
        void Check(bool condition, string name)
        {
            if (!condition) throw new Exception("FAILED: " + name);
            passed++;
        }

        var temp = Path.Combine(Path.GetTempPath(), "ConnectorWatch-characterization-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try
        {
            var start = new DateTimeOffset(2026, 9, 9, 0, 0, 0, TimeSpan.Zero);
            var csv = Path.Combine(temp, "recorded-telemetry.csv");
            const string header = "timestamp_utc,gpu_uuid,board_power_w,input_voltage_v,analysis_power_w,gpu_temp_c,extra_voltages_json,status\n";
            var lines = new[]
            {
                Csv.Line($"{start:O}", "GPU-recorded", 90, 12.10, 100, 40, "{}", "NO_SHIFT_DETECTED"),
                Csv.Line($"{start.AddSeconds(1):O}", "GPU-recorded", 190, 12.20, 200, 41, "{\"pcie_12v_v\":12.0}", "NO_SHIFT_DETECTED"),
                Csv.Line($"{start.AddSeconds(1):O}", "GPU-recorded", 190, 12.20, 200, null, "{}", "NO_SHIFT_DETECTED"),
                Csv.Line($"{start.AddSeconds(6):O}", "GPU-recorded", 290, 12.40, 300, 42, "{}", "NO_SHIFT_DETECTED"),
                Csv.Line($"{start.AddSeconds(3):O}", "GPU-recorded", 140, 12.30, 150, 43, "{}", "NO_SHIFT_DETECTED"),
                Csv.Line("not-a-timestamp", "GPU-recorded", 1, 12.0, 1, 40, "{}", "BAD"),
            };
            File.WriteAllText(csv, header + string.Join("\n", lines) + "\n");

            var options = new RecordedDataCharacterizationOptions
            {
                ExpectedCadenceSeconds = 1,
                GapMultiplier = 2,
                MinimumGapSeconds = 2,
            };
            var report = RecordedDataCharacterization.AnalyzeFile(csv, options);
            Check(report.Evidence == CharacterizationEvidence.RECORDED,
                "unmarked persisted rows are recorded evidence");
            Check(report.Rows.RowsRead == 6 && report.Rows.RowsWithValidTimestamp == 5 &&
                report.Rows.InvalidRows == 1, "CSV row accounting");
            Check(report.Duplicates.DuplicateRowCount == 1 && report.Duplicates.DuplicateGroupCount == 1 &&
                report.Duplicates.OutOfOrderRowCount == 1, "duplicate and ordering characterization");
            Check(report.Cadence.DistinctTimestampCount == 4 && report.Cadence.IntervalCount == 3 &&
                report.Cadence.NominalSeconds == 1 && report.Cadence.MedianSeconds == 2,
                "cadence characterization");
            Check(report.Gaps.GapCount == 1 && report.Gaps.LongestGapSeconds == 3 &&
                report.Gaps.TotalGapSeconds == 3, "gap characterization");
            Check(report.LoadRangeWatts.Minimum == 100 && report.LoadRangeWatts.Maximum == 300 &&
                report.VoltageRangeVolts.Minimum == 12.1 && report.VoltageRangeVolts.Maximum == 12.4,
                "load and voltage ranges");
            Check(report.LoadVoltageAssociation.Status == "AVAILABLE" &&
                report.LoadVoltageAssociation.PairedRowCount == 5 &&
                report.LoadVoltageAssociation.SlopeVoltsPerWatt > 0,
                "load-voltage association");
            Check(report.ThermalCoverage.AvailableRows == 4 && report.ThermalCoverage.MissingRows == 1 &&
                report.ThermalCoverage.CoveragePercent == 80,
                "thermal coverage");
            var sensor = report.SensorAvailability.Sensors.Single(x => x.Name == "pcie_voltage_v");
            Check(sensor.AvailableRows == 1 && sensor.MissingRows == 4,
                "optional sensor availability and JSON extras");

            var firstJson = report.ToDeterministicJson();
            var secondJson = RecordedDataCharacterization.AnalyzeFile(csv, options).ToJson();
            Check(firstJson == secondJson, "deterministic JSON report");
            using (var document = JsonDocument.Parse(firstJson))
                Check(document.RootElement.GetProperty("evidence").GetString() == "RECORDED" &&
                    document.RootElement.GetProperty("gaps").GetProperty("gap_count").GetInt32() == 1,
                    "JSON report wire shape");

            var synthetic = Path.Combine(temp, "synthetic.json");
            File.WriteAllText(synthetic, "{\"synthetic\":true,\"rows\":[" +
                $"{{\"timestamp_utc\":\"{start:O}\",\"load_w\":440,\"input_voltage_v\":12.1,\"gpu_temp_c\":60}}," +
                $"{{\"timestamp_utc\":\"{start.AddSeconds(1):O}\",\"load_w\":440,\"input_voltage_v\":12.0}}]}}");
            var syntheticReport = RecordedDataCharacterization.AnalyzeFile(synthetic);
            Check(syntheticReport.Evidence == CharacterizationEvidence.SYNTHETIC &&
                syntheticReport.Rows.RowsWithValidTimestamp == 2 &&
                syntheticReport.ThermalCoverage.AvailableRows == 1,
                "synthetic JSON rows remain explicitly non-recorded");

            var empty = Path.Combine(temp, "empty.csv");
            File.WriteAllText(empty, header);
            var noData = RecordedDataCharacterization.AnalyzeFile(empty);
            Check(noData.Evidence == CharacterizationEvidence.NO_DATA && noData.Rows.RowsRead == 0 &&
                noData.LoadRangeWatts.Count == 0 && noData.Cadence.NominalSeconds is null,
                "empty input is explicit no-data");
            Check(RecordedDataCharacterization.FindTelemetryFiles(temp).Count == 1,
                "telemetry file discovery is bounded to telemetry names");
        }
        finally
        {
            Directory.Delete(temp, true);
        }
        Console.WriteLine($"PASS: {passed} recorded-data characterization checks.");
    }
}
