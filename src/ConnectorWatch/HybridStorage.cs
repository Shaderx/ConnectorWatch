using System.Text;

namespace ConnectorWatch;

/// <summary>Bounded pending history; disk checkpoints never supply the live transport.</summary>
public sealed class HybridStorage
{
    public const string Header = "timestamp_utc,gpu_uuid,board_power_w,input_voltage_v,voltage_timestamp_utc,analysis_power_w,analysis_power_source,gpu_temp_c,utilization_pct,power_limit_w,voltage_source,extra_voltages_json,bin_w,reference_v,rolling_median_v,rolling_p05_v,median_drop_v,status,detail,connector_current_a,connector_power_w,pcie_voltage_v,pcie_current_a,pcie_power_w,electrical_source,electrical_freshness_kind,electrical_source_timestamp_utc,connector_power_provenance,analysis_load_unit,acquisition_health,poll_start_monotonic,poll_end_monotonic,poll_latency_seconds,value_change_flags,consecutive_identical_observations,analysis_coverage_percent,eligible_loaded_count,analyzed_loaded_count,current_unanalyzed_loaded_seconds,longest_unanalyzed_loaded_seconds,last_completed_sample_monotonic,sample_age_seconds\n";
    readonly Dictionary<string, StringBuilder> pending = new();
    int pendingChars;
    double lastFlush;
    readonly string directory;
    readonly double interval;
    public HybridStorage(string directory, double interval) { this.directory = directory; this.interval = interval; }
    public bool Due(double elapsed) => elapsed - lastFlush >= interval || pendingChars >= 1024 * 1024;
    public void Add(DateTimeOffset time, string row)
    {
        AddPending(Path.Combine(directory, $"telemetry-{time:yyyy-MM-dd}.csv"), row);
    }
    public void AddEvent(string row) => AddPending(Path.Combine(directory, "events.csv"), row);
    void AddPending(string path, string row)
    {
        if (row.Length > 256 * 1024 || pendingChars + row.Length > 2 * 1024 * 1024)
            throw new IOException("Pending telemetry exceeded its bounded RAM capacity.");
        if (!pending.TryGetValue(path, out var buffer)) pending[path] = buffer = new();
        buffer.Append(row); pendingChars += row.Length;
    }
    public void Flush(double elapsed, string status, string baseline)
    {
        foreach (var entry in pending)
        {
            // Retry opening, never retry an append after bytes may have been written.
            using var file = OpenAppend(entry.Key);
            using var writer = new StreamWriter(file, new UTF8Encoding(false));
            if (file.Length == 0 && Path.GetFileName(entry.Key).StartsWith("telemetry-", StringComparison.Ordinal)) writer.Write(Header);
            writer.Write(entry.Value.ToString());
        }
        pending.Clear(); pendingChars = 0;
        Atomic(Path.Combine(directory, "baseline.json"), baseline);
        Atomic(Path.Combine(directory, "status.json"), status);
        lastFlush = elapsed;
    }
    static FileStream OpenAppend(string path)
    {
        for (int attempt = 0; ; attempt++)
        {
            try { return new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read | FileShare.Delete); }
            catch (Exception ex) when (Retryable(ex) && attempt < 4) { Thread.Sleep(25 << attempt); }
        }
    }
    // Windows rename can report ACCESS_DENIED when a reader denies delete sharing.
    static bool Retryable(Exception ex) => ex is IOException && (ex.HResult & 0xffff) is 32 or 33 || ex is UnauthorizedAccessException;
    public static void Atomic(string path, string value)
    {
        for (int attempt = 0; ; attempt++)
        {
            try { File.WriteAllText(path + ".tmp", value); File.Move(path + ".tmp", path, true); return; }
            catch (Exception ex) when (Retryable(ex) && attempt < 4) { Thread.Sleep(25 << attempt); }
        }
    }
}
