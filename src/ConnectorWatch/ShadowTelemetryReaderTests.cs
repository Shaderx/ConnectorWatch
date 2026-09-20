using System.Globalization;
using System.IO.Compression;
using System.Text;

namespace ConnectorWatch;

public static class ShadowTelemetryReaderTests
{
    static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;
    static readonly string[] Header = HybridStorage.Header.TrimEnd('\r', '\n').Split(',');

    public static void Run()
    {
        var passed = 0;
        void Check(bool condition, string name)
        {
            if (!condition) throw new Exception("FAILED: " + name);
            passed++;
        }

        var root = Path.Combine(Path.GetTempPath(), "ConnectorWatch-shadow-reader-" + Guid.NewGuid());
        Directory.CreateDirectory(root);
        try
        {
            var cutoff = new DateTimeOffset(2026, 9, 20, 0, 0, 0, TimeSpan.Zero);
            var days = Enumerable.Range(10, 8).Select(day => new DateOnly(2026, 9, day)).ToArray();
            foreach (var day in days)
            {
                var power = day.Day == 11 ? 220 : 200;
                var rows = day.Day == 17
                    ? VaryingMinuteRows(day, "GPU-A", "electrical-a", "CONNECTOR_POWER", "Measured")
                    : MinuteRows(day, power, "GPU-A", "electrical-a", "CONNECTOR_POWER", "Measured");
                var path = Path.Combine(root, $"telemetry-{day:yyyy-MM-dd}.csv");
                if (day.Day <= 14) WriteGzip(path + ".gz", rows);
                else WriteCsv(path, rows);
            }

            // The plain file wins over an archive for a day.  Its different
            // value makes accidental double-reading observable.
            var preferredDay = new DateOnly(2026, 9, 11);
            WriteCsv(Path.Combine(root, $"telemetry-{preferredDay:yyyy-MM-dd}.csv"),
                MinuteRows(preferredDay, 220, "GPU-A", "electrical-a", "CONNECTOR_POWER", "Measured"));
            WriteGzip(Path.Combine(root, $"telemetry-{preferredDay:yyyy-MM-dd}.csv.gz"),
                MinuteRows(preferredDay, 999, "GPU-A", "electrical-a", "CONNECTOR_POWER", "Measured"));

            var beforeBytes = File.ReadAllBytes(Path.Combine(root, "telemetry-2026-09-17.csv"));
            var beforeWrite = File.GetLastWriteTimeUtc(Path.Combine(root, "telemetry-2026-09-17.csv"));
            var result = ShadowTelemetryReader.ReadDirectory(root,
                new ShadowReadOptions { InputCutoffUtc = cutoff });
            Check(result.Files.Count == 8, "completed UTC days and one source per day");
            Check(result.Files.All(x => x.Name.EndsWith(".csv", StringComparison.OrdinalIgnoreCase) ||
                x.Name.EndsWith(".csv.gz", StringComparison.OrdinalIgnoreCase)), "source inventory metadata");
            Check(result.Rows.Count == 8 && result.Rows.All(x => x.ConnectorPowerW == 200 ||
                x.ConnectorPowerW == 220), "gzip and csv aggregation equivalence");
            Check(result.Rows.Single(x => x.TimestampUtc.Date == new DateTime(2026, 9, 11)).ConnectorPowerW == 220,
                "plain CSV preferred over same-day gzip");
            Check(result.Rows.All(x => x.ObservationCount >= 5), "minimum distinct observations and count");
            Check(result.Rows.Select(x => x.CohortKey).Distinct().Count() == 1,
                "load variation does not create new cohort identities");
            Check(result.Warnings.Any(x => x.Contains("unverified", StringComparison.OrdinalIgnoreCase)),
                "host-poll freshness warning");
            Check(beforeBytes.SequenceEqual(File.ReadAllBytes(Path.Combine(root, "telemetry-2026-09-17.csv"))) &&
                beforeWrite == File.GetLastWriteTimeUtc(Path.Combine(root, "telemetry-2026-09-17.csv")),
                "reader does not mutate telemetry");

            var malformedDay = new DateOnly(2026, 9, 18);
            var malformedPath = Path.Combine(root, $"telemetry-{malformedDay:yyyy-MM-dd}.csv");
            WriteCsv(malformedPath, MinuteRows(malformedDay, 200, "GPU-A", "electrical-a",
                "CONNECTOR_POWER", "Measured"));
            File.AppendAllText(malformedPath,
                Row(malformedDay.ToDateTime(TimeOnly.MinValue).AddSeconds(30), 200,
                    "GPU-A", "electrical-a", "CONNECTOR_POWER", "Measured",
                    new Dictionary<string, string> { ["sample_age_seconds"] = "not-a-number" }) + "\n" +
                Row(malformedDay.ToDateTime(TimeOnly.MinValue).AddSeconds(31), 200,
                    "GPU-A", "electrical-a", "CONNECTOR_POWER", "Measured",
                    new Dictionary<string, string> { ["status"] = "STALE" }) + "\n" +
                new string('x', ShadowTelemetryReader.MaximumRecordCharacters + 32) + "\n" +
                "\"truncated");
            var malformed = ShadowTelemetryReader.ReadDirectory(root,
                new ShadowReadOptions { InputCutoffUtc = cutoff });
            Check(malformed.InvalidRows >= 2 && malformed.RejectedRows >= 1 && malformed.PartialRows >= 1,
                "malformed, oversized, stale, and truncated rows counted");

            var splitDay = new DateOnly(2026, 9, 19);
            var splitPath = Path.Combine(root, $"telemetry-{splitDay:yyyy-MM-dd}.csv");
            var splitRows = new List<string>();
            foreach (var identity in new[]
                     {
                         ("GPU-A", "electrical-a", "CONNECTOR_POWER", "Measured", 200d),
                         ("GPU-B", "electrical-a", "CONNECTOR_POWER", "Measured", 200d),
                         ("GPU-A", "electrical-b", "CONNECTOR_POWER", "Measured", 200d),
                         ("GPU-A", "electrical-a", "CONNECTOR_CURRENT", "DerivedFromVoltageAndCurrent", 200d),
                     })
                splitRows.AddRange(MinuteRows(splitDay, identity.Item5, identity.Item1,
                    identity.Item2, identity.Item3, identity.Item4));
            WriteCsv(splitPath, splitRows);
            var split = ShadowTelemetryReader.ReadDirectory(root,
                new ShadowReadOptions { InputCutoffUtc = cutoff });
            var splitOutput = split.Rows.Where(x => x.TimestampUtc.Date == new DateTime(2026, 9, 19)).ToArray();
            Check(splitOutput.Length == 4 && splitOutput.Select(x => x.CohortKey).Distinct().Count() == 4,
                "GPU, electrical, analysis, and provenance identities split cohorts");
            Check(splitOutput.Any(x => x.PowerProvenance == "DerivedFromVoltageAndCurrent"),
                "derived connector power provenance is explicit");

            var capRoot = Path.Combine(root, "cap");
            Directory.CreateDirectory(capRoot);
            var capDay = new DateOnly(2026, 9, 12);
            var capRows = Enumerable.Range(0, 3).SelectMany(minute =>
                MinuteRows(capDay, 200 + minute * 20, "GPU-C", "electrical-c", "CONNECTOR_POWER", "Measured",
                    minute)).ToArray();
            WriteCsv(Path.Combine(capRoot, $"telemetry-{capDay:yyyy-MM-dd}.csv"), capRows);
            var capped = ShadowTelemetryReader.ReadDirectory(capRoot,
                new ShadowReadOptions { InputCutoffUtc = cutoff, MaximumMinutes = 2 });
            Check(capped.Rows.Count == 2 && capped.Truncated &&
                capped.Warnings.Any(x => x.Contains("truncated", StringComparison.OrdinalIgnoreCase)),
                "aggregate minute cap is explicit");

            var invalidOptions = false;
            try
            {
                _ = ShadowTelemetryReader.ReadDirectory(root,
                    new ShadowReadOptions { MaximumMinutes = 0 });
            }
            catch (ArgumentOutOfRangeException) { invalidOptions = true; }
            Check(invalidOptions, "options are validated");
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }

        Console.WriteLine($"PASS: {passed} shadow telemetry checks.");
    }

    static IEnumerable<string> MinuteRows(DateOnly day, double power, string gpu,
        string electrical, string analysisSource, string provenance, int minute = 0)
    {
        var start = new DateTimeOffset(day.ToDateTime(new TimeOnly(0, minute)), TimeSpan.Zero);
        return Enumerable.Range(0, 5).Select(index => Row(start.AddSeconds(index), power,
            gpu, electrical, analysisSource, provenance));
    }

    static IEnumerable<string> VaryingMinuteRows(DateOnly day, string gpu,
        string electrical, string analysisSource, string provenance)
    {
        var start = new DateTimeOffset(day.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
        return Enumerable.Range(0, 10).Select(index => Row(start.AddSeconds(index),
            index < 7 ? 200 : 350, gpu, electrical, analysisSource, provenance));
    }

    static string Row(DateTimeOffset timestamp, double power, string gpu,
        string electrical, string analysisSource, string provenance,
        IReadOnlyDictionary<string, string>? overrides = null)
    {
        var values = Header.Select(name =>
        {
            if (overrides is not null && overrides.TryGetValue(name, out var overrideValue))
                return overrideValue;
            return name switch
            {
                "timestamp_utc" => timestamp.ToString("O", Invariant),
                "gpu_uuid" => gpu,
                "board_power_w" => (power + 10).ToString(Invariant),
                "input_voltage_v" => "12",
                "voltage_timestamp_utc" => timestamp.ToString("O", Invariant),
                "analysis_power_w" => power.ToString(Invariant),
                "analysis_power_source" => analysisSource,
                "gpu_temp_c" => "40",
                "voltage_source" => electrical,
                "status" => "LEARNING_REFERENCE",
                "detail" => "learning",
                "connector_current_a" => (power / 12).ToString("0.###", Invariant),
                "connector_power_w" => power.ToString(Invariant),
                "electrical_source" => electrical,
                "electrical_freshness_kind" => "HostPollTimestampUnverified",
                "connector_power_provenance" => provenance,
                "analysis_load_unit" => "W",
                "acquisition_health" => "SENSOR_UNCHARACTERIZED",
                "poll_latency_seconds" => "0.01",
                "value_change_flags" => "All",
                "consecutive_identical_observations" => "1",
                "sample_age_seconds" => "0",
                _ => string.Empty,
            };
        }).ToArray();
        return Csv.Line(values);
    }

    static void WriteCsv(string path, IEnumerable<string> rows)
    {
        File.WriteAllText(path, HybridStorage.Header + string.Join("\n", rows) + "\n",
            new UTF8Encoding(false));
    }

    static void WriteGzip(string path, IEnumerable<string> rows)
    {
        using var file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
        using var gzip = new GZipStream(file, CompressionLevel.Optimal);
        using var writer = new StreamWriter(gzip, new UTF8Encoding(false));
        writer.Write(HybridStorage.Header);
        writer.Write(string.Join("\n", rows));
        writer.Write('\n');
    }
}
