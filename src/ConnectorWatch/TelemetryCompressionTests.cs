using System.IO.Compression;
using System.Text;

namespace ConnectorWatch;

public static class TelemetryCompressionTests
{
    public static void Run()
    {
        string directory = Path.Combine(Path.GetTempPath(), "ConnectorWatch-compression-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        void Check(bool value, string name) { if (!value) throw new Exception("FAILED: " + name); }
        string PathFor(DateOnly date) => Path.Combine(directory, $"telemetry-{date:yyyy-MM-dd}.csv");
        void WriteGzip(string path, string value)
        {
            using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            using var gzip = new GZipStream(output, CompressionLevel.Fastest);
            using var writer = new StreamWriter(gzip, new UTF8Encoding(false));
            writer.Write(value);
        }
        try
        {
            var now = DateTimeOffset.UtcNow;
            var oldDate = DateOnly.FromDateTime(now.UtcDateTime.Date).AddDays(-3);
            string source = PathFor(oldDate);
            string content = HybridStorage.Header + "2026-09-01T00:00:00Z,fixture\n";
            File.WriteAllText(source, content, new UTF8Encoding(false));
            var failures = new List<Exception>();
            var maintenance = new TelemetryCompressionMaintenance(directory, failures.Add);
            var roundTrip = maintenance.RunOnce(now);
            string archive = source + ".gz";
            Check(roundTrip.Compressed == 1 && !File.Exists(source) && File.Exists(archive),
                "old telemetry is compressed and original is removed after verification");
            using (var input = new FileStream(archive, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var gzip = new GZipStream(input, CompressionMode.Decompress))
            using (var reader = new StreamReader(gzip, Encoding.UTF8))
                Check(reader.ReadToEnd() == content, "gzip archive round-trips the exact CSV");

            var currentDate = DateOnly.FromDateTime(now.UtcDateTime.Date);
            foreach (int offset in new[] { 0, -1, -2, 1 })
                File.WriteAllText(PathFor(currentDate.AddDays(offset)), "retained\n", new UTF8Encoding(false));
            maintenance.RunOnce(now);
            foreach (int offset in new[] { 0, -1, -2, 1 })
            {
                string retained = PathFor(currentDate.AddDays(offset));
                Check(File.Exists(retained) && !File.Exists(retained + ".gz"),
                    "current and preceding two UTC days remain uncompressed");
            }

            var resumedDate = currentDate.AddDays(-4);
            string resumedSource = PathFor(resumedDate);
            string resumedArchive = resumedSource + ".gz";
            string resumedContent = "interrupted-publication\n";
            File.WriteAllText(resumedSource, resumedContent, new UTF8Encoding(false));
            WriteGzip(resumedArchive, resumedContent);
            byte[] resumedArchiveBytes = File.ReadAllBytes(resumedArchive);
            var resumed = maintenance.RunOnce(now);
            Check(resumed.Compressed == 1 && !File.Exists(resumedSource) &&
                File.ReadAllBytes(resumedArchive).SequenceEqual(resumedArchiveBytes),
                "matching existing archive resumes source cleanup without recompression");

            var conflictDate = currentDate.AddDays(-5);
            string conflictSource = PathFor(conflictDate);
            string conflictArchive = conflictSource + ".gz";
            File.WriteAllText(conflictSource, "source-preserved\n", new UTF8Encoding(false));
            WriteGzip(conflictArchive, "different-archive-content\n");
            byte[] conflictArchiveBytes = File.ReadAllBytes(conflictArchive);
            var conflict = maintenance.RunOnce(now);
            Check(conflict.Conflicts == 1 && File.ReadAllText(conflictSource) == "source-preserved\n" &&
                File.ReadAllBytes(conflictArchive).SequenceEqual(conflictArchiveBytes),
                "different existing archive is never overwritten and source is preserved");

            var invalidDate = currentDate.AddDays(-6);
            string invalidSource = PathFor(invalidDate);
            string invalidArchive = invalidSource + ".gz";
            File.WriteAllText(invalidSource, "invalid-archive-source\n", new UTF8Encoding(false));
            File.WriteAllText(invalidArchive, "not gzip\n", new UTF8Encoding(false));
            var invalid = maintenance.RunOnce(now);
            Check(invalid.Conflicts == 2 && File.Exists(invalidSource) &&
                File.ReadAllText(invalidArchive) == "not gzip\n",
                "invalid existing archive remains untouched with source preserved");

            string activeSource = PathFor(currentDate);
            File.WriteAllText(activeSource, "active\n", new UTF8Encoding(false));
            var active = maintenance.RunOnce(now);
            Check(active.Compressed == 0 && File.Exists(activeSource) && !File.Exists(activeSource + ".gz"),
                "active UTC day is never compressed");

            if (OperatingSystem.IsWindows())
            {
                string lockedSource = PathFor(currentDate.AddDays(-7));
                File.WriteAllText(lockedSource, "locked\n", new UTF8Encoding(false));
                using (var locked = new FileStream(lockedSource, FileMode.Open, FileAccess.Read, FileShare.None))
                {
                    var failed = maintenance.RunOnce(now);
                    Check(failed.Failures == 1 && File.Exists(lockedSource) && !File.Exists(lockedSource + ".gz"),
                        "locked source failure preserves original telemetry");
                }
                Check(failures.Count > 0, "compression failures use recoverable diagnostic callback");
            }
            using (var shutdown = new CancellationTokenSource())
            using (var background = new TelemetryCompressionMaintenance(directory, failures.Add))
            {
                background.Start(shutdown.Token);
                shutdown.Cancel();
            }
            Console.WriteLine("PASS: telemetry compression retention, round-trip, conflict and safety checks.");
        }
        finally { Directory.Delete(directory, true); }
    }
}
