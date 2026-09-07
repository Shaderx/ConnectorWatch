namespace ConnectorWatch;

public static class HybridStorageTests
{
    public static void Run()
    {
        string directory = Path.Combine(Path.GetTempPath(), "ConnectorWatch-storage-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        void Check(bool value, string name) { if (!value) throw new Exception("FAILED: " + name); }
        try
        {
            foreach (int interval in new[] { 0, 61 })
            {
                bool invalid = false;
                try { new Config { FlushSeconds = interval }.Validate(); } catch { invalid = true; }
                Check(invalid, "invalid checkpoint interval rejected");
            }
            var storage = new HybridStorage(directory, 30);
            var time = new DateTimeOffset(2026, 9, 7, 23, 59, 59, TimeSpan.Zero);
            storage.Add(time, "first\n"); storage.AddEvent("transition\n");
            Check(Directory.GetFiles(directory).Length == 0 && !storage.Due(29), "samples and routine events stay in RAM before checkpoint");
            storage.Add(time.AddSeconds(1), "second\n");
            Check(storage.Due(30), "checkpoint interval is elapsed time");
            storage.Flush(30, "{\"stopped\":false}", "{}");
            Check(File.ReadAllText(Path.Combine(directory, "telemetry-2026-09-07.csv")) == HybridStorage.Header + "first\n", "pre-midnight batch retains its UTC day");
            Check(File.ReadAllText(Path.Combine(directory, "telemetry-2026-09-08.csv")) == HybridStorage.Header + "second\n", "post-midnight batch receives header");
            Check(File.ReadAllText(Path.Combine(directory, "events.csv")) == "transition\n", "event batch persisted");
            storage.Add(time.AddSeconds(2), "third\n");
            storage.Flush(31, "{\"stopped\":true}", "{\"saved\":true}");
            Check(File.ReadAllText(Path.Combine(directory, "telemetry-2026-09-08.csv")) == HybridStorage.Header + "second\nthird\n", "forced shutdown flush preserves pending rows without duplicate header");
            storage.Flush(32, "{}", "{}");
            Check(File.ReadAllLines(Path.Combine(directory, "telemetry-2026-09-08.csv")).Length == 3, "empty flush does not duplicate history");
            bool rejected = false;
            try { storage.Add(time, new string('x', 256 * 1024 + 1)); } catch (IOException) { rejected = true; }
            Check(rejected, "oversized records cannot grow pending RAM without bound");
            var path = Path.Combine(directory, "status.json");
            if (OperatingSystem.IsWindows())
            {
                using (var locked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    bool failed = false;
                    try { HybridStorage.Atomic(path, "replacement"); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { failed = true; }
                    Check(failed && File.ReadAllText(path) == "{}", "persistent sharing violation is surfaced and previous checkpoint survives");
                }
                using var acquired = new ManualResetEventSlim();
                var release = Task.Run(() => {
                    using var locked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                    acquired.Set(); Thread.Sleep(100);
                });
                acquired.Wait(); HybridStorage.Atomic(path, "recovered"); release.GetAwaiter().GetResult();
                Check(File.ReadAllText(path) == "recovered", "transient sharing violation recovers within bounded retry");
            }
            Console.WriteLine("PASS: hybrid buffering, checkpoint, midnight, bounds and file-lock checks.");
        }
        finally { Directory.Delete(directory, true); }
    }
}
