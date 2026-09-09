using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace ConnectorWatch.Gui;

/// <summary>
/// Long-horizon daily evidence reader for the degradation confidence view.
/// The live <see cref="TelemetryStore"/> intentionally retains only a short
/// chart window; this class reads dated CSV files independently and persists
/// compact daily summaries for subsequent refreshes.
/// </summary>
public sealed class ConfidenceHistory
{
    // The scorer displays 90 completed days and uses a seven-day warm-up. A
    // 97-day retained window gives it the preceding context at the boundary.
    public const int HistoryDays = 97;
    public static readonly TimeSpan CheckpointInterval = TimeSpan.FromMinutes(30);
    const int CacheSchemaVersion = 4;
    const int MaximumCsvRecordCharacters = 262_144;
    const int MaximumRowsPerDay = 2_000_000;
    const long MaximumCacheBytes = 64L * 1024 * 1024;
    const string UnknownEpoch = "epoch-unknown";

    readonly string dataDirectory;
    readonly string cachePath;
    readonly double binWatts;
    readonly SemaphoreSlim refreshGate = new(1, 1);
    readonly object stateGate = new();
    IReadOnlyList<ConfidenceDay> days = Array.Empty<ConfidenceDay>();
    string status = "Not loaded";
    bool loading;
    CacheDocument? cache;
    CacheDocument? persistedCache;
    bool cacheLoaded;
    bool cacheHasCheckpoint;
    bool cacheDirty;
    DateTimeOffset? lastCheckpointAtUtc;
    DateTimeOffset? lastRefreshAtUtc;

    public ConfidenceHistory(string dataDirectory, string cachePath, double binWatts = 25)
    {
        if (string.IsNullOrWhiteSpace(dataDirectory))
            throw new ArgumentException("A telemetry directory is required.", nameof(dataDirectory));
        if (string.IsNullOrWhiteSpace(cachePath))
            throw new ArgumentException("A cache path is required.", nameof(cachePath));
        if (!double.IsFinite(binWatts) || binWatts <= 0 || binWatts > 1_000)
            throw new ArgumentOutOfRangeException(nameof(binWatts));

        this.dataDirectory = Path.GetFullPath(dataDirectory);
        this.cachePath = Path.GetFullPath(cachePath);
        this.binWatts = binWatts;
    }

    /// <summary>Published atomically after a complete worker refresh.</summary>
    public IReadOnlyList<ConfidenceDay> Days
    {
        get { lock (stateGate) return days; }
    }

    /// <summary>Human-readable state suitable for a non-blocking status line.</summary>
    public string Status
    {
        get { lock (stateGate) return status; }
    }

    public bool Loading
    {
        get { lock (stateGate) return loading; }
    }

    /// <summary>
    /// Persists the latest in-memory history regardless of the normal
    /// checkpoint interval. The operation is serialized with refreshes and
    /// performs its file work off the caller's synchronization context.
    /// </summary>
    public async Task FlushAsync()
    {
        await refreshGate.WaitAsync().ConfigureAwait(false);
        try
        {
            await Task.Run(FlushCore).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsRecoverable(ex))
        {
            GuiLog.Current.Write("confidence_history_cache_flush_error",
                new { dataDirectory, cachePath }, ex, throttle: true);
            lock (stateGate)
                status = "Historical confidence loaded; cache unavailable: " + ex.Message;
        }
        finally
        {
            refreshGate.Release();
        }
    }

    /// <summary>
    /// Performs the file read and backfill on a worker thread. Concurrent
    /// timer calls are serialized so no caller observes a partially rebuilt
    /// list or writes the cache over another refresh.
    /// </summary>
    public async Task RefreshAsync(DateTimeOffset now)
    {
        await refreshGate.WaitAsync().ConfigureAwait(false);
        lock (stateGate)
        {
            loading = true;
            status = "Loading historical telemetry";
        }

        try
        {
            DateTimeOffset refreshAt = now == default
                ? DateTimeOffset.UtcNow
                : now.ToUniversalTime();
            var result = await Task.Run(() => RefreshCore(refreshAt)).ConfigureAwait(false);
            lock (stateGate)
            {
                // RefreshCore creates a new array and never mutates it after
                // return. WPF may safely enumerate the previous array while
                // this assignment occurs.
                days = result.Days;
                status = result.Status;
            }
        }
        catch (Exception ex) when (IsRecoverable(ex))
        {
            GuiLog.Current.Write("confidence_history_error",
                new { dataDirectory, cachePath }, ex, throttle: true);
            lock (stateGate)
                status = "Historical confidence unavailable: " + ex.Message;
        }
        finally
        {
            lock (stateGate) loading = false;
            refreshGate.Release();
        }
    }

    void FlushCore()
    {
        if (!cacheLoaded || cache is null || !cacheDirty)
            return;

        DateTimeOffset checkpointAt = lastRefreshAtUtc ?? DateTimeOffset.UtcNow;
        string? error = CheckpointCache(cache, checkpointAt);
        if (error is null)
            return;

        lock (stateGate)
            status = "Historical confidence loaded; cache unavailable: " + error;
    }

    RefreshResult RefreshCore(DateTimeOffset now)
    {
        DateTimeOffset today = UtcDay(now);
        DateTimeOffset firstDay = today.AddDays(-HistoryDays);

        if (!Directory.Exists(dataDirectory))
            return new(Array.Empty<ConfidenceDay>(),
                "Historical telemetry directory is unavailable.");

        CacheDocument workingCache = EnsureCacheLoaded(now).Clone();
        var cachedFiles = workingCache.Files
            .Where(file => !string.IsNullOrWhiteSpace(file.Name))
            .GroupBy(FileLogicalName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.ToArray(),
                StringComparer.OrdinalIgnoreCase);
        var cachedDays = workingCache.Days ?? new();
        var cachedDayKeys = new HashSet<DateTimeOffset>(
            cachedDays.Select(day => UtcDay(day.Day)));

        var paths = EnumerateTelemetryFiles(firstDay, today)
            .OrderBy(path => FileDate(path))
            .ThenBy(path => Path.GetFileName(path), StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var currentFiles = paths.Select(path =>
        {
            var stamp = Stamp(path);
            return new CachedFile
            {
                Name = Path.GetFileName(path),
                LogicalName = LogicalFileName(path),
                Day = FileDate(path),
                Length = stamp.Length,
                WriteTicks = stamp.WriteTicks,
            };
        }).ToList();

        var currentByLogical = currentFiles
            .GroupBy(FileLogicalName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.ToArray(),
                StringComparer.OrdinalIgnoreCase);
        bool cacheHasUsableDays = cachedDays.Count > 0;
        var dirtyFileDays = new HashSet<DateTimeOffset>();
        foreach (var file in currentFiles)
        {
            if (!cachedFiles.TryGetValue(FileLogicalName(file), out var old) ||
                !old.Any(candidate => SamePhysicalFile(candidate, file)))
                AddAffectedDays(dirtyFileDays, file.Day, firstDay, today);
        }

        // Deleted source files do not erase an already summarized completed
        // day. This keeps a temporary rotate/delete operation from replacing
        // authoritative evidence with a smaller surviving subset.
        foreach (var old in cachedFiles.Values.SelectMany(value => value))
        {
            if (currentByLogical.ContainsKey(FileLogicalName(old))) continue;
            if (!cachedDayKeys.Contains(UtcDay(old.Day)))
                AddAffectedDays(dirtyFileDays, UtcDay(old.Day), firstDay, today);
        }

        // A cache from an older, partially written refresh may have stamps but
        // no daily evidence. Rebuild every present date in that case.
        if (!cacheHasUsableDays)
            foreach (var file in currentFiles)
                AddAffectedDays(dirtyFileDays, file.Day, firstDay, today);

        var records = cachedDays
            .Where(day => UtcDay(day.Day) >= firstDay && UtcDay(day.Day) <= today)
            .Select(day => day.Clone())
            .ToList();
        var errors = new List<string>();
        var failedDays = new HashSet<DateTimeOffset>();

        // Dirty dates are rebuilt one UTC day at a time. Raw samples are held
        // only for that day (and adjacent date files for midnight overlap),
        // then reduced to minute/daily evidence and discarded.
        foreach (DateTimeOffset day in dirtyFileDays.OrderBy(value => value))
        {
            // If any original contributor disappeared, surviving files cannot
            // reconstruct an authoritative day. Keep the saved summary and
            // original contributor stamp until its retention window expires.
            if (records.Any(item => UtcDay(item.Day) == day && day < today) && cachedFiles.Values
                    .SelectMany(value => value).Any(file =>
                    FileDateWithin(file.Day, day.AddDays(-1), day.AddDays(1)) &&
                    (!currentByLogical.TryGetValue(FileLogicalName(file), out var present) ||
                     IsTruncatedPhysicalFile(file, present))))
            {
                errors.Add(day.ToString("yyyy-MM-dd") + ": retained saved comparison because a source file is missing or truncated");
                failedDays.Add(day);
                continue;
            }
            var contributing = currentFiles
                .Where(file => FileDateWithin(file.Day, day.AddDays(-1), day.AddDays(1)))
                .Select(file => Path.Combine(dataDirectory, file.Name))
                .Where(File.Exists)
                .ToArray();
            if (contributing.Length == 0)
                continue; // preserve an authoritative cached day on deletion

            try
            {
                var rebuilt = BuildDay(day, contributing, errors);
                records.RemoveAll(item => UtcDay(item.Day) == day);
                records.AddRange(rebuilt.Records);
            }
            catch (Exception ex) when (IsRecoverable(ex))
            {
                failedDays.Add(day);
                errors.Add(day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) +
                    ": " + ex.Message);
                // Keep the previous record and its old stamp so another timer
                // tick can retry a transient sharing or decoding failure.
            }
        }

        // Promote a partial day to completed when the clock crosses midnight.
        foreach (var record in records)
            record.Completed = UtcDay(record.Day) < today;
        records.RemoveAll(record => UtcDay(record.Day) < firstDay ||
            UtcDay(record.Day) > today);

        AssignEpochs(records, workingCache);
        var published = records
            .Where(record => record.Completed && UtcDay(record.Day) < today)
            .Select(record => record.ToConfidenceDay(binWatts))
            .OrderBy(day => day.Day)
            .ThenBy(day => day.Cohort, StringComparer.Ordinal)
            .ThenBy(day => day.Epoch, StringComparer.Ordinal)
            .ToArray();

        workingCache.SchemaVersion = CacheSchemaVersion;
        workingCache.BinWatts = binWatts;
        // A failed read must remain dirty on the next refresh. Keep missing
        // contributors too, so a later adjacent-file append cannot erase a day.
        workingCache.Files = currentFiles.Where(file => !failedDays.Any(day =>
            FileDateWithin(file.Day, day.AddDays(-1), day.AddDays(1)))).ToList();
        workingCache.Files.AddRange(cachedFiles.Values.SelectMany(value => value)
            .Where(file => !currentByLogical.ContainsKey(FileLogicalName(file)) ||
                failedDays.Any(day => FileDateWithin(file.Day, day.AddDays(-1), day.AddDays(1)))
                ).Select(file => file.Clone()));
        workingCache.Files = workingCache.Files
            .GroupBy(file => file.Name, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.Last()).ToList();
        workingCache.Days = records;
        TrimCache(workingCache, firstDay, today);

        bool stateChanged = !CacheContentEquals(persistedCache, workingCache);
        cache = workingCache;
        cacheDirty = stateChanged;
        lastRefreshAtUtc = now;
        string? cacheError = null;
        if (stateChanged && ShouldCheckpoint(now))
            cacheError = CheckpointCache(workingCache, now);

        string resultStatus;
        if (errors.Count > 0)
            resultStatus = "Historical confidence loaded with file warnings: " +
                string.Join("; ", errors.Take(2));
        else if (cacheError is not null)
            resultStatus = "Historical confidence loaded; cache unavailable: " + cacheError;
        else
            resultStatus = $"Historical confidence loaded ({published.Length} daily records).";
        return new(Array.AsReadOnly(published), resultStatus);
    }

    CacheDocument EnsureCacheLoaded(DateTimeOffset now)
    {
        if (cacheLoaded && cache is not null)
            return cache;

        cache = LoadCache();
        persistedCache = cache.Clone();
        cacheLoaded = true;
        // A valid on-disk checkpoint establishes the start of the next
        // 30-minute interval. A first successful backfill with no checkpoint
        // is allowed to write immediately.
        lastCheckpointAtUtc = cacheHasCheckpoint ? now : null;
        return cache;
    }

    bool ShouldCheckpoint(DateTimeOffset now)
    {
        if (!cacheDirty) return false;
        if (!cacheHasCheckpoint || !lastCheckpointAtUtc.HasValue) return true;
        return now >= lastCheckpointAtUtc.Value.Add(CheckpointInterval);
    }

    string? CheckpointCache(CacheDocument document, DateTimeOffset checkpointAtUtc)
    {
        string? error = TrySaveCache(document, checkpointAtUtc);
        if (error is not null)
            return error;

        persistedCache = document.Clone();
        cacheHasCheckpoint = true;
        cacheDirty = false;
        lastCheckpointAtUtc = checkpointAtUtc;
        return null;
    }

    static void AddAffectedDays(HashSet<DateTimeOffset> dirty, DateTimeOffset fileDay,
        DateTimeOffset first, DateTimeOffset last)
    {
        for (int offset = -1; offset <= 1; offset++)
        {
            DateTimeOffset day = UtcDay(fileDay.AddDays(offset));
            if (day >= first && day <= last) dirty.Add(day);
        }
    }

    static bool FileDateWithin(DateTimeOffset value, DateTimeOffset from,
        DateTimeOffset to) => value >= UtcDay(from) && value <= UtcDay(to);

    IEnumerable<string> EnumerateTelemetryFiles(DateTimeOffset firstDay,
        DateTimeOffset lastDay)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in Directory.EnumerateFiles(dataDirectory, "telemetry-*",
                     SearchOption.TopDirectoryOnly))
        {
            if (!seen.Add(path) || !TryFileDate(path, out var date) ||
                date < firstDay || date > lastDay)
                continue;
            yield return path;
        }
    }

    static bool TryFileDate(string path, out DateTimeOffset date)
    {
        date = default;
        string name = Path.GetFileName(path);
        if (name.EndsWith(".gz", StringComparison.OrdinalIgnoreCase))
            name = name[..^3];
        if (name.Length < 21 || !name.StartsWith("telemetry-",
                StringComparison.OrdinalIgnoreCase) ||
            !name.EndsWith(".csv", StringComparison.OrdinalIgnoreCase))
            return false;
        string value = name.Substring("telemetry-".Length, 10);
        if (!DateTime.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture,
            DateTimeStyles.None, out var parsed))
            return false;
        date = new DateTimeOffset(DateTime.SpecifyKind(parsed, DateTimeKind.Utc));
        return true;
    }

    static string LogicalFileName(string path)
    {
        string name = Path.GetFileName(path);
        return name.EndsWith(".gz", StringComparison.OrdinalIgnoreCase)
            ? name[..^3]
            : name;
    }

    static string FileLogicalName(CachedFile file) => LogicalFileName(file.Name);

    static bool SamePhysicalFile(CachedFile left, CachedFile right) =>
        string.Equals(left.Name, right.Name, StringComparison.OrdinalIgnoreCase) &&
        left.Length == right.Length && left.WriteTicks == right.WriteTicks;

    static bool IsTruncatedPhysicalFile(CachedFile original,
        IEnumerable<CachedFile> current)
    {
        foreach (var candidate in current)
        {
            if (string.Equals(original.Name, candidate.Name,
                    StringComparison.OrdinalIgnoreCase))
                return candidate.Length < original.Length;
        }
        return false;
    }

    static DateTimeOffset FileDate(string path) =>
        TryFileDate(path, out var date) ? date : DateTimeOffset.MaxValue;

    static DateTimeOffset UtcDay(DateTimeOffset value) =>
        new(value.UtcDateTime.Date, TimeSpan.Zero);

    static FileStamp Stamp(string path)
    {
        var info = new FileInfo(path);
        return new(info.Length, info.LastWriteTimeUtc.Ticks);
    }

    DayBuild BuildDay(DateTimeOffset day, IReadOnlyList<string> paths,
        List<string> errors)
    {
        var rows = new Dictionary<ObservationKey, RawObservation>();
        bool bounded = false;
        foreach (string path in paths)
        {
            ParseFileForDay(path, day, rows, () => bounded = true);
        }
        if (bounded)
            errors.Add(day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) +
                ": raw day evidence was bounded");
        var result = ReduceDay(day, rows.Values);
        if (bounded)
        {
            foreach (var record in result.Records) { record.Reason = "TRUNCATED_UNAVAILABLE"; record.MedianDropMv = null; }
            // A truncated suffix may hide a source/reference transition. End
            // the source run so later days cannot reuse its old comparison.
            result.Add(new CachedDay { Day = day, Source = "Unavailable history", ContextStart = day.AddDays(1).AddTicks(-1), Reason = "TRUNCATED_UNAVAILABLE" });
        }
        return result;
    }

    void ParseFileForDay(string path, DateTimeOffset day,
        Dictionary<ObservationKey, RawObservation> rows, Action bounded)
    {
        Dictionary<string, int>? headers = null;
        foreach (string record in ReadCsvRecords(path, bounded))
        {
            if (headers is null)
            {
                headers = TelemetryStore.ParseCsv(record).Select((name, index) => (name, index))
                    .GroupBy(item => NormalizeHeader(item.name)).ToDictionary(group => group.Key, group => group.First().index);
                continue;
            }
            var cells = TelemetryStore.ParseCsv(record);
            if (!TryObservation(headers, cells, out var observation) ||
                UtcDay(observation.SensorTime) != day)
                continue;
            var key = ObservationKey.For(observation);
            if (!rows.TryGetValue(key, out var previous) ||
                observation.HostTime > previous.HostTime)
            {
                if (!rows.ContainsKey(key) && rows.Count >= MaximumRowsPerDay)
                {
                    bounded();
                    continue;
                }
                rows[key] = observation;
            }
        }
    }

    static IEnumerable<string> ReadCsvRecords(string path, Action oversized)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        using var input = path.EndsWith(".gz", StringComparison.OrdinalIgnoreCase)
            ? (Stream)new GZipStream(file, CompressionMode.Decompress)
            : file;
        using var reader = new StreamReader(input, new UTF8Encoding(false, true),
            detectEncodingFromByteOrderMarks: true, bufferSize: 16 * 1024);
        var record = new StringBuilder();
        bool quoted = false;
        bool skip = false;
        int character;
        while ((character = reader.Read()) >= 0)
        {
            char value = (char)character;
            if (value == '"') quoted = !quoted;
            if (value == '\n' && !quoted)
            {
                if (!skip && record.Length > 0)
                    yield return record.ToString().TrimEnd('\r');
                record.Clear();
                skip = false;
                continue;
            }
            if (skip) continue;
            if (record.Length >= MaximumCsvRecordCharacters)
            {
                record.Clear();
                skip = true;
                quoted = false;
                oversized();
                continue;
            }
            record.Append(value);
        }
        if (!skip && !quoted && record.Length > 0)
            yield return record.ToString().TrimEnd('\r');
    }

    bool TryObservation(Dictionary<string, int> headers, string[] cells,
        out RawObservation observation)
    {
        observation = new RawObservation();
        string Get(params string[] names)
        {
            foreach (string wanted in names)
            {
                string normalized = NormalizeHeader(wanted);
                if (headers.TryGetValue(normalized, out int index) && index < cells.Length) return cells[index];
            }
            return "";
        }

        if (!TryDate(Get("timestamp_utc", "timestamp"), out var host) ||
            !TryDate(Get("voltage_timestamp_utc", "sensor_timestamp_utc", "sensor_time"), out var sensor) ||
            !TryNumber(Get("input_voltage_v", "voltage_v", "voltage"), out var voltage) ||
            !double.IsFinite(voltage) || voltage < 6 || voltage > 16)
            return false;

        string gpu = Get("gpu_uuid", "gpu_id");
        string electrical = FirstNonEmpty(Get("electrical_source"), Get("voltage_source"));
        string loadSource = Get("analysis_power_source", "analysis_load_source");
        string loadUnit = Get("analysis_load_unit", "load_unit");
        observation = new RawObservation
        {
            HostTime = host,
            SensorTime = sensor,
            Voltage = voltage,
            Reference = Number(Get("reference_v", "reference_volts")),
            Power = Number(Get("analysis_power_w", "power_w", "load_w")),
            Bin = Integer(Get("bin_w", "load_bin_w", "analysis_bin_w")),
            BinWidth = binWatts,
            SourceIdentity = new[] { gpu, electrical, loadSource, loadUnit }.Any(string.IsNullOrWhiteSpace)
                ? "Unknown" : NormalizeIdentity(TelemetryStore.ComposeSourceIdentity(gpu, electrical, loadSource, loadUnit)),
            Status = FirstNonEmpty(Get("status"), "UNKNOWN"),
            FreshnessKind = FirstNonEmpty(Get("electrical_freshness_kind", "freshness_kind"), "Unknown"),
            AcquisitionHealth = FirstNonEmpty(Get("acquisition_health", "health"), "Unknown"),
            LoadUnit = NormalizeIdentity(loadUnit),
        };
        return true;
    }

    static DayBuild ReduceDay(DateTimeOffset day,
        IEnumerable<RawObservation> source)
    {
        var result = new DayBuild(day);
        var segments = new Dictionary<string, SegmentBuilder>(StringComparer.Ordinal);
        string? activeSource = null;
        DateTimeOffset contextStart = day;
        foreach (var row in source.OrderBy(row => row.SensorTime).ThenBy(row => row.HostTime).ThenBy(row => row.SourceIdentity, StringComparer.Ordinal))
        {
            // Source changes end every load comparison. Switching load bins
            // within the same source does not retrain the other bins.
            if (activeSource != row.SourceIdentity)
            {
                foreach (var completed in segments.Values) result.Add(completed.ToCachedDay(day));
                segments.Clear();
                activeSource = row.SourceIdentity;
                contextStart = row.SensorTime;
            }
            string group = GroupKey(row);
            if (segments.TryGetValue(group, out var segment) && row.Reference is double reference &&
                segment.Reference is double prior && Math.Abs(prior - reference) > 1e-9)
            { result.Add(segment.ToCachedDay(day)); segments.Remove(group); segment = null; }
            if (segment is null) segments[group] = segment = new SegmentBuilder(group, activeSource, contextStart);
            segment.Observe(row);
        }
        foreach (var completed in segments.Values) result.Add(completed.ToCachedDay(day));
        return result;
    }

    static string GroupKey(RawObservation row)
    {
        string source = NormalizeIdentity(row.SourceIdentity);
        string bin = row.Bin.HasValue
            ? row.Bin.Value.ToString(CultureInfo.InvariantCulture) + "W"
            : "unknown";
        return source + "|bin=" + bin;
    }

    static string NormalizeHeader(string value) =>
        new string((value ?? "").Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    static string NormalizeIdentity(string? value) =>
        string.IsNullOrWhiteSpace(value) ? "Unknown" : value.Trim();

    static string FirstNonEmpty(string preferred, string fallback = "") =>
        string.IsNullOrWhiteSpace(preferred) ? fallback : preferred.Trim();

    static bool TryDate(string text, out DateTimeOffset date) =>
        DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out date) && date != default;

    static bool TryNumber(string text, out double number)
    {
        number = 0;
        return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture,
            out number) && double.IsFinite(number);
    }

    static double? Number(string text) => TryNumber(text, out var number) ? number : null;

    static int? Integer(string text)
    {
        if (!TryNumber(text, out var number) || number < int.MinValue || number > int.MaxValue)
            return null;
        double rounded = Math.Round(number);
        return Math.Abs(number - rounded) < 1e-9 ? (int)rounded : null;
    }

    static DateTimeOffset FloorMinute(DateTimeOffset value)
    {
        DateTime utc = value.UtcDateTime;
        long ticks = utc.Ticks - utc.Ticks % TimeSpan.TicksPerMinute;
        return new DateTimeOffset(new DateTime(ticks, DateTimeKind.Utc));
    }

    static bool IsWattUnit(string? unit) =>
        string.Equals(unit?.Trim(), "W", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(unit?.Trim(), "WATTS", StringComparison.OrdinalIgnoreCase);

    static double Quantile(IReadOnlyList<double> values, double quantile)
    {
        var sorted = values.OrderBy(value => value).ToArray();
        if (sorted.Length == 0) return double.NaN;
        double index = Math.Clamp(quantile, 0, 1) * (sorted.Length - 1);
        int low = (int)Math.Floor(index);
        int high = (int)Math.Ceiling(index);
        return sorted[low] + (sorted[high] - sorted[low]) * (index - low);
    }

    static bool IsLearning(string? status) =>
        string.Equals(status?.Trim(), "LEARNING_REFERENCE", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(status?.Trim(), "LEARNING", StringComparison.OrdinalIgnoreCase);

    static bool IsSettling(string? status) =>
        string.Equals(status?.Trim(), "LOAD_SETTLING", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(status?.Trim(), "SETTLING", StringComparison.OrdinalIgnoreCase);

    static bool IsStaleOrUnavailable(string? status, string? freshness, string? health)
    {
        string[] values = { status ?? "", freshness ?? "", health ?? "" };
        return values.Any(value => value.IndexOf("STALE", StringComparison.OrdinalIgnoreCase) >= 0 ||
            value.IndexOf("UNAVAILABLE", StringComparison.OrdinalIgnoreCase) >= 0 ||
            value.IndexOf("SOURCE_GAP", StringComparison.OrdinalIgnoreCase) >= 0 ||
            value.IndexOf("INVALID", StringComparison.OrdinalIgnoreCase) >= 0 ||
            value.Equals("SOURCE_UNAVAILABLE", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("NATIVE_FAILURE", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("POWER_UNAVAILABLE", StringComparison.OrdinalIgnoreCase));
    }

    static bool IsEligible(RawObservation row, out string reason)
    {
        if (NormalizeIdentity(row.SourceIdentity).Equals("Unknown",
                StringComparison.OrdinalIgnoreCase))
            return Reject("UNKNOWN_SOURCE", out reason);
        if (!row.Bin.HasValue) return Reject("LOAD_BIN_UNAVAILABLE", out reason);
        if (!IsWattUnit(row.LoadUnit) || row.Power is not double power || power < row.Bin.Value || power >= row.Bin.Value + row.BinWidth)
            return Reject("LOAD_NOT_COMPARABLE", out reason);
        if (row.Reference is not double reference || !double.IsFinite(reference))
            return Reject("REFERENCE_UNAVAILABLE", out reason);
        if (IsLearning(row.Status)) return Reject("LEARNING", out reason);
        if (IsSettling(row.Status)) return Reject("SETTLING", out reason);
        if (IsStaleOrUnavailable(row.Status, row.FreshnessKind, row.AcquisitionHealth))
            return Reject("STALE_OR_UNAVAILABLE", out reason);

        string status = row.Status?.Trim() ?? "";
        if (status is not ("REFERENCE_UNVERIFIED" or "WINDOW_WARMUP" or
            "NO_SHIFT_DETECTED" or "SUDDEN_DROOP" or "BASELINE_SHIFT" or
            "ANALYZED" or "LOAD_QUALIFIED"))
            return Reject("STATUS_UNAVAILABLE", out reason);
        reason = "";
        return true;
    }

    static bool Reject(string value, out string reason)
    {
        reason = value;
        return false;
    }

    static void AssignEpochs(List<CachedDay> records, CacheDocument cache)
    {
        if (records.Count == 0) return;
        var boundary = records.Min(record => record.Day);
        // Only checkpoints before the replay boundary may seed a replay.
        // Later transitions/anchors are reconstructed from authoritative days.
        var checkpoints = cache.Epochs.Where(entry => entry.Start < boundary)
            .GroupBy(entry => entry.Group, StringComparer.Ordinal)
            .Select(group => group.OrderBy(entry => entry.Start).Last()).ToList();
        var current = checkpoints.ToDictionary(entry => entry.Group, StringComparer.Ordinal);
        foreach (var checkpoint in checkpoints)
        {
            if (checkpoint.AnchorDay >= boundary)
            {
                checkpoint.AnchorDay = null; checkpoint.AnchorMedianLoad = null;
                checkpoint.AnchorP10Load = null; checkpoint.AnchorP90Load = null;
            }
        }
        var sourceCheckpoint = cache.SourceRuns.Where(run => run.Start < boundary).OrderBy(run => run.Start).LastOrDefault();
        var sourceRuns = sourceCheckpoint is null ? new List<SourceRun>() : new List<SourceRun> { sourceCheckpoint };
        var activeSource = sourceCheckpoint;
        var ledger = new List<EpochLedgerEntry>(checkpoints);
        var unambiguousDays = records.GroupBy(record => (record.Cohort, record.Day))
            .Where(group => group.Count() == 1).Select(group => group.Key).ToHashSet();
        foreach (var record in records.OrderBy(record => record.ContextStart).ThenBy(record => record.EpochStart).ThenBy(record => record.Cohort, StringComparer.Ordinal))
        {
            if (activeSource is null || activeSource.Source != record.Source)
            {
                activeSource = new SourceRun { Source = record.Source, Start = record.ContextStart };
                sourceRuns.Add(activeSource);
            }
            if (record.Reference is not double reference)
            { record.Epoch = UnknownEpoch; continue; }
            if (!current.TryGetValue(record.Cohort, out var epoch) || epoch.Reference != reference || epoch.SourceStart != activeSource.Start)
            {
                var start = record.EpochStart ?? record.Day;
                epoch = new EpochLedgerEntry
                {
                    Group = record.Cohort, Reference = reference, Start = start,
                    SourceStart = activeSource.Start,
                    Epoch = EpochId(record.Cohort, start, reference),
                };
                current[record.Cohort] = epoch; ledger.Add(epoch);
            }
            epoch.LastSeen = record.LastSample ?? record.Day;
            if (!epoch.AnchorDay.HasValue && record.Completed && record.Reason.Length == 0 && unambiguousDays.Contains((record.Cohort, record.Day)) &&
                record.MinuteCount >= DegradationConfidence.MinimumMinutes && record.ObservationCount >= DegradationConfidence.MinimumObservations &&
                record.FirstSample.HasValue && record.LastSample.HasValue &&
                (record.LastSample.Value - record.FirstSample.Value).TotalMinutes >= DegradationConfidence.MinimumSampleSpanMinutes &&
                record.MedianLoad.HasValue && record.P10Load.HasValue && record.P90Load.HasValue)
            {
                epoch.AnchorDay = record.Day; epoch.AnchorMedianLoad = record.MedianLoad;
                epoch.AnchorP10Load = record.P10Load; epoch.AnchorP90Load = record.P90Load;
            }
            record.Epoch = epoch.Epoch;
            record.AnchorMedianLoad = epoch.AnchorMedianLoad;
            record.AnchorP10Load = epoch.AnchorP10Load; record.AnchorP90Load = epoch.AnchorP90Load;
        }
        cache.Epochs = ledger;
        cache.SourceRuns = sourceRuns;
    }

    static string EpochId(string group, DateTimeOffset start, double reference) =>
        "epoch-" + start.ToUniversalTime().UtcDateTime.ToString(
            "yyyyMMdd'T'HHmmssfffffff'Z'", CultureInfo.InvariantCulture) +
        "-ref-" + reference.ToString("R", CultureInfo.InvariantCulture) +
        "-" + StableHash(group);

    static string StableHash(string value)
    {
        unchecked
        {
            uint hash = 2166136261;
            foreach (char character in value)
            {
                hash ^= character;
                hash *= 16777619;
            }
            return hash.ToString("x8", CultureInfo.InvariantCulture);
        }
    }

    static void TrimCache(CacheDocument cache, DateTimeOffset first,
        DateTimeOffset last)
    {
        cache.Files ??= new();
        cache.Days ??= new();
        cache.Epochs ??= new();
        cache.Days.RemoveAll(day => UtcDay(day.Day) < first || UtcDay(day.Day) > last);
        cache.Files.RemoveAll(file => file.Day < first || file.Day > last);
        cache.Epochs = cache.Epochs.Where(entry => entry.Start >= first)
            .Concat(cache.Epochs.Where(entry => entry.Start < first).GroupBy(entry => entry.Group)
                .Select(group => group.OrderBy(entry => entry.Start).Last())).ToList();
        var checkpoint = cache.SourceRuns.Where(run => run.Start < first).OrderBy(run => run.Start).LastOrDefault();
        cache.SourceRuns = cache.SourceRuns.Where(run => run.Start >= first).ToList();
        if (checkpoint is not null) cache.SourceRuns.Insert(0, checkpoint);
    }

    static bool CacheContentEquals(CacheDocument? left, CacheDocument? right)
    {
        if (ReferenceEquals(left, right)) return true;
        if (left is null || right is null) return false;
        if (left.SchemaVersion != right.SchemaVersion ||
            !left.BinWatts.Equals(right.BinWatts)) return false;

        var leftFiles = (left.Files ?? new()).OrderBy(file => FileLogicalName(file),
                StringComparer.OrdinalIgnoreCase).ThenBy(file => file.Name,
                StringComparer.OrdinalIgnoreCase).ToArray();
        var rightFiles = (right.Files ?? new()).OrderBy(file => FileLogicalName(file),
                StringComparer.OrdinalIgnoreCase).ThenBy(file => file.Name,
                StringComparer.OrdinalIgnoreCase).ToArray();
        if (leftFiles.Length != rightFiles.Length) return false;
        for (int index = 0; index < leftFiles.Length; index++)
        {
            var a = leftFiles[index];
            var b = rightFiles[index];
            if (!string.Equals(a.Name, b.Name, StringComparison.OrdinalIgnoreCase) ||
                a.Day != b.Day || a.Length != b.Length || a.WriteTicks != b.WriteTicks)
                return false;
        }

        var leftDays = (left.Days ?? new()).OrderBy(day => UtcDay(day.Day))
            .ThenBy(day => day.Cohort, StringComparer.Ordinal)
            .ThenBy(day => day.Epoch, StringComparer.Ordinal)
            .ThenBy(day => day.ContextStart).ToArray();
        var rightDays = (right.Days ?? new()).OrderBy(day => UtcDay(day.Day))
            .ThenBy(day => day.Cohort, StringComparer.Ordinal)
            .ThenBy(day => day.Epoch, StringComparer.Ordinal)
            .ThenBy(day => day.ContextStart).ToArray();
        if (leftDays.Length != rightDays.Length) return false;
        for (int index = 0; index < leftDays.Length; index++)
            if (!CachedDayEquals(leftDays[index], rightDays[index])) return false;

        var leftEpochs = (left.Epochs ?? new()).OrderBy(entry => entry.Group,
                StringComparer.Ordinal).ThenBy(entry => entry.Start).ThenBy(entry => entry.Epoch,
                StringComparer.Ordinal).ToArray();
        var rightEpochs = (right.Epochs ?? new()).OrderBy(entry => entry.Group,
                StringComparer.Ordinal).ThenBy(entry => entry.Start).ThenBy(entry => entry.Epoch,
                StringComparer.Ordinal).ToArray();
        if (leftEpochs.Length != rightEpochs.Length) return false;
        for (int index = 0; index < leftEpochs.Length; index++)
            if (!EpochEquals(leftEpochs[index], rightEpochs[index])) return false;

        var leftRuns = (left.SourceRuns ?? new()).OrderBy(run => run.Start)
            .ThenBy(run => run.Source, StringComparer.Ordinal).ToArray();
        var rightRuns = (right.SourceRuns ?? new()).OrderBy(run => run.Start)
            .ThenBy(run => run.Source, StringComparer.Ordinal).ToArray();
        if (leftRuns.Length != rightRuns.Length) return false;
        for (int index = 0; index < leftRuns.Length; index++)
        {
            if (leftRuns[index].Start != rightRuns[index].Start ||
                !string.Equals(leftRuns[index].Source, rightRuns[index].Source,
                    StringComparison.Ordinal))
                return false;
        }
        return true;
    }

    static bool CachedDayEquals(CachedDay left, CachedDay right) =>
        left.Day == right.Day &&
        string.Equals(left.Cohort, right.Cohort, StringComparison.Ordinal) &&
        string.Equals(left.Source, right.Source, StringComparison.Ordinal) &&
        left.ContextStart == right.ContextStart &&
        string.Equals(left.Epoch, right.Epoch, StringComparison.Ordinal) &&
        left.EpochStart == right.EpochStart &&
        left.Reference == right.Reference &&
        left.MedianDropMv == right.MedianDropMv &&
        left.MinuteCount == right.MinuteCount &&
        left.ObservationCount == right.ObservationCount &&
        left.FirstSample == right.FirstSample &&
        left.LastSample == right.LastSample &&
        left.MinLoad == right.MinLoad &&
        left.MaxLoad == right.MaxLoad &&
        left.MedianLoad == right.MedianLoad &&
        left.P10Load == right.P10Load &&
        left.P90Load == right.P90Load &&
        left.AnchorMedianLoad == right.AnchorMedianLoad &&
        left.AnchorP10Load == right.AnchorP10Load &&
        left.AnchorP90Load == right.AnchorP90Load &&
        left.Unverified == right.Unverified &&
        string.Equals(left.Reason, right.Reason, StringComparison.Ordinal) &&
        left.Completed == right.Completed;

    static bool EpochEquals(EpochLedgerEntry left, EpochLedgerEntry right) =>
        string.Equals(left.Group, right.Group, StringComparison.Ordinal) &&
        string.Equals(left.Epoch, right.Epoch, StringComparison.Ordinal) &&
        left.Reference == right.Reference && left.Start == right.Start &&
        left.LastSeen == right.LastSeen && left.SourceStart == right.SourceStart &&
        left.AnchorDay == right.AnchorDay &&
        left.AnchorMedianLoad == right.AnchorMedianLoad &&
        left.AnchorP10Load == right.AnchorP10Load &&
        left.AnchorP90Load == right.AnchorP90Load;

    CacheDocument LoadCache()
    {
        cacheHasCheckpoint = false;
        try
        {
            if (!File.Exists(cachePath)) return NewCache();
            var info = new FileInfo(cachePath);
            if (info.Length <= 0 || info.Length > MaximumCacheBytes)
                return NewCache();
            using var stream = new FileStream(cachePath, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            var document = JsonSerializer.Deserialize<CacheDocument>(stream, JsonOptions());
            if (document is null || document.SchemaVersion != CacheSchemaVersion ||
                !double.IsFinite(document.BinWatts) ||
                Math.Abs(document.BinWatts - binWatts) > 1e-9)
                return NewCache();
            document.Files ??= new();
            document.Days ??= new();
            document.Epochs ??= new();
            document.SourceRuns ??= new();
            cacheHasCheckpoint = true;
            return document;
        }
        catch (Exception ex) when (IsRecoverable(ex))
        {
            GuiLog.Current.Write("confidence_history_cache_read_error",
                new { cachePath }, ex, throttle: true);
            return NewCache();
        }
    }

    CacheDocument NewCache() => new()
    {
        SchemaVersion = CacheSchemaVersion,
        BinWatts = binWatts,
        Files = new(),
        Days = new(),
        Epochs = new(),
    };

    string? TrySaveCache(CacheDocument document, DateTimeOffset checkpointAtUtc)
    {
        try
        {
            var persisted = document.Clone();
            persisted.UpdatedAtUtc = checkpointAtUtc.ToUniversalTime();
            string json = JsonSerializer.Serialize(persisted, JsonOptions());
            if (Encoding.UTF8.GetByteCount(json) > MaximumCacheBytes)
                return "cache payload exceeds its bounded size";
            string? directory = Path.GetDirectoryName(cachePath);
            if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
            string temporary = cachePath + ".tmp";
            File.WriteAllText(temporary, json, new UTF8Encoding(false));
            File.Move(temporary, cachePath, true);
            document.UpdatedAtUtc = persisted.UpdatedAtUtc;
            return null;
        }
        catch (Exception ex) when (IsRecoverable(ex))
        {
            GuiLog.Current.Write("confidence_history_cache_write_error",
                new { cachePath }, ex, throttle: true);
            return ex.Message;
        }
    }

    static JsonSerializerOptions JsonOptions() => new()
    {
        WriteIndented = false,
        PropertyNameCaseInsensitive = true,
    };

    static bool IsRecoverable(Exception ex) => ex is IOException or UnauthorizedAccessException or
        JsonException or InvalidDataException or FormatException or DecoderFallbackException or
        NotSupportedException or ArgumentException;

    readonly record struct RefreshResult(IReadOnlyList<ConfidenceDay> Days, string Status);
    readonly record struct FileStamp(long Length, long WriteTicks);
    readonly record struct ObservationKey(string SourceIdentity, DateTimeOffset SensorTime)
    {
        public static ObservationKey For(RawObservation observation) =>
            new(NormalizeIdentity(observation.SourceIdentity),
                observation.SensorTime.ToUniversalTime());
    }

    sealed class RawObservation
    {
        public DateTimeOffset HostTime { get; set; }
        public DateTimeOffset SensorTime { get; set; }
        public double Voltage { get; set; }
        public double? Reference { get; set; }
        public double? Power { get; set; }
        public int? Bin { get; set; }
        public double BinWidth { get; set; }
        public string SourceIdentity { get; set; } = "Unknown";
        public string Status { get; set; } = "UNKNOWN";
        public string FreshnessKind { get; set; } = "Unknown";
        public string AcquisitionHealth { get; set; } = "Unknown";
        public string LoadUnit { get; set; } = "Unknown";
    }

    sealed class CachedFile
    {
        public string Name { get; set; } = "";
        public string LogicalName { get; set; } = "";
        public DateTimeOffset Day { get; set; }
        public long Length { get; set; }
        public long WriteTicks { get; set; }

        public CachedFile Clone() => new()
        {
            Name = Name,
            LogicalName = LogicalName,
            Day = Day,
            Length = Length,
            WriteTicks = WriteTicks,
        };
    }

    sealed class CacheDocument
    {
        public int SchemaVersion { get; set; }
        public double BinWatts { get; set; }
        public DateTimeOffset UpdatedAtUtc { get; set; }
        public List<CachedFile> Files { get; set; } = new();
        public List<CachedDay> Days { get; set; } = new();
        public List<EpochLedgerEntry> Epochs { get; set; } = new();
        public List<SourceRun> SourceRuns { get; set; } = new();

        public CacheDocument Clone() => new()
        {
            SchemaVersion = SchemaVersion,
            BinWatts = BinWatts,
            UpdatedAtUtc = UpdatedAtUtc,
            Files = (Files ?? new()).Select(file => file.Clone()).ToList(),
            Days = (Days ?? new()).Select(day => day.Clone()).ToList(),
            Epochs = (Epochs ?? new()).Select(entry => entry.Clone()).ToList(),
            SourceRuns = (SourceRuns ?? new()).Select(run => run.Clone()).ToList(),
        };
    }

    sealed class SourceRun
    {
        public string Source { get; set; } = "";
        public DateTimeOffset Start { get; set; }

        public SourceRun Clone() => new() { Source = Source, Start = Start };
    }

    sealed class EpochLedgerEntry
    {
        public string Group { get; set; } = "";
        public string Epoch { get; set; } = UnknownEpoch;
        public double? Reference { get; set; }
        public DateTimeOffset Start { get; set; }
        public DateTimeOffset LastSeen { get; set; }
        public DateTimeOffset SourceStart { get; set; }
        public DateTimeOffset? AnchorDay { get; set; }
        public double? AnchorMedianLoad { get; set; }
        public double? AnchorP10Load { get; set; }
        public double? AnchorP90Load { get; set; }

        public EpochLedgerEntry Clone() => new()
        {
            Group = Group,
            Epoch = Epoch,
            Reference = Reference,
            Start = Start,
            LastSeen = LastSeen,
            SourceStart = SourceStart,
            AnchorDay = AnchorDay,
            AnchorMedianLoad = AnchorMedianLoad,
            AnchorP10Load = AnchorP10Load,
            AnchorP90Load = AnchorP90Load,
        };
    }

    sealed class CachedDay
    {
        public DateTimeOffset Day { get; set; }
        public string Cohort { get; set; } = "";
        public string Source { get; set; } = "";
        public DateTimeOffset ContextStart { get; set; }
        public string Epoch { get; set; } = UnknownEpoch;
        public DateTimeOffset? EpochStart { get; set; }
        public double? Reference { get; set; }
        public double? MedianDropMv { get; set; }
        public int MinuteCount { get; set; }
        public int ObservationCount { get; set; }
        public DateTimeOffset? FirstSample { get; set; }
        public DateTimeOffset? LastSample { get; set; }
        public double? MinLoad { get; set; }
        public double? MaxLoad { get; set; }
        public double? MedianLoad { get; set; }
        public double? P10Load { get; set; }
        public double? P90Load { get; set; }
        public double? AnchorMedianLoad { get; set; }
        public double? AnchorP10Load { get; set; }
        public double? AnchorP90Load { get; set; }
        public bool Unverified { get; set; }
        public string Reason { get; set; } = "";
        public bool Completed { get; set; }

        public CachedDay Clone() => new()
        {
            Day = Day,
            Cohort = Cohort,
            Source = Source,
            ContextStart = ContextStart,
            Epoch = Epoch,
            EpochStart = EpochStart,
            Reference = Reference,
            MedianDropMv = MedianDropMv,
            MinuteCount = MinuteCount,
            ObservationCount = ObservationCount,
            FirstSample = FirstSample,
            LastSample = LastSample,
            MinLoad = MinLoad,
            MaxLoad = MaxLoad,
            MedianLoad = MedianLoad,
            P10Load = P10Load,
            P90Load = P90Load,
            AnchorMedianLoad = AnchorMedianLoad,
            AnchorP10Load = AnchorP10Load,
            AnchorP90Load = AnchorP90Load,
            Unverified = Unverified,
            Reason = Reason,
            Completed = Completed,
        };

        public ConfidenceDay ToConfidenceDay(double width) => new()
        {
            Day = Day,
            Cohort = Cohort,
            Epoch = Epoch,
            MedianDropMv = MedianDropMv,
            MinuteCount = MinuteCount,
            ObservationCount = ObservationCount,
            FirstSample = FirstSample,
            LastSample = LastSample,
            MinLoad = MinLoad,
            MaxLoad = MaxLoad,
            MedianLoad = MedianLoad,
            P10Load = P10Load,
            P90Load = P90Load,
            BinWidth = width,
            AnchorMedianLoad = AnchorMedianLoad,
            AnchorP10Load = AnchorP10Load,
            AnchorP90Load = AnchorP90Load,
            Unverified = Unverified,
            Reason = Reason,
        };
    }

    sealed class DayBuild
    {
        public DateTimeOffset Day { get; }
        public List<CachedDay> Records { get; } = new();

        public DayBuild(DateTimeOffset day) => Day = day;
        public void Add(CachedDay record) => Records.Add(record);
    }

    sealed class SegmentBuilder
    {
        readonly string cohort;
        readonly string source;
        readonly DateTimeOffset contextStart;
        readonly Dictionary<DateTimeOffset, MinuteBuilder> minutes = new();
        DateTimeOffset firstRow;
        DateTimeOffset? lastRow;
        DateTimeOffset? epochStart;
        double? reference;
        bool unknownSource;
        bool missingReference;
        bool staleOrUnavailable;
        bool learningOrSettling;
        bool invalidLoad;

        public SegmentBuilder(string cohort, string source, DateTimeOffset contextStart)
        { this.cohort = cohort; this.source = source; this.contextStart = contextStart; }
        public double? Reference => reference;

        public void Observe(RawObservation row)
        {
            if (firstRow == default) firstRow = row.SensorTime.ToUniversalTime();
            lastRow = row.SensorTime.ToUniversalTime();
            if (NormalizeIdentity(row.SourceIdentity).Equals("Unknown",
                    StringComparison.OrdinalIgnoreCase)) unknownSource = true;
            if (row.Reference is double candidate && double.IsFinite(candidate))
            {
                reference ??= candidate;
                epochStart ??= row.SensorTime.ToUniversalTime();
            }
            else missingReference = true;
            if (IsLearning(row.Status) || IsSettling(row.Status)) learningOrSettling = true;
            if (IsStaleOrUnavailable(row.Status, row.FreshnessKind, row.AcquisitionHealth))
                staleOrUnavailable = true;
            if (!row.Bin.HasValue) invalidLoad = true;
            if (!IsEligible(row, out _)) return;

            DateTimeOffset minute = FloorMinute(row.SensorTime);
            if (!minutes.TryGetValue(minute, out var bucket))
                minutes.Add(minute, bucket = new(minute));
            bucket.Add(row, row.Reference!.Value);
        }

        public CachedDay ToCachedDay(DateTimeOffset day)
        {
            var supported = minutes.Values.Where(minute => minute.Count >= 5)
                .OrderBy(minute => minute.Start).ToArray();
            var drops = supported.Select(minute => Quantile(minute.Drops, .5)).ToArray();
            var loadMedians = supported.Where(minute => minute.Loads.Count > 0)
                .Select(minute => Quantile(minute.Loads, .5)).ToArray();
            var times = supported.SelectMany(minute => minute.SampleTimes).ToArray();
            bool unverified = supported.Any(minute => minute.Unverified);
            string reason = supported.Length > 0 ? "" :
                unknownSource ? "UNKNOWN_SOURCE" :
                missingReference ? "REFERENCE_UNAVAILABLE" :
                staleOrUnavailable ? "STALE_OR_UNAVAILABLE" :
                learningOrSettling ? "LEARNING_OR_SETTLING" :
                invalidLoad ? "LOAD_BIN_UNAVAILABLE" :
                "INSUFFICIENT_MINUTE_OBSERVATIONS";
            return new CachedDay
            {
                Day = day,
                Cohort = cohort,
                Source = source,
                ContextStart = contextStart,
                EpochStart = epochStart ?? (firstRow == default ? day : firstRow),
                Reference = reference,
                MedianDropMv = drops.Length == 0 ? null : Quantile(drops, .5),
                MinuteCount = supported.Length,
                ObservationCount = supported.Sum(minute => minute.Count),
                FirstSample = times.Length == 0 ? null : times.Min(),
                LastSample = times.Length == 0 ? null : times.Max(),
                MinLoad = loadMedians.Length == 0 ? null : supported
                    .SelectMany(minute => minute.Loads).Min(),
                MaxLoad = loadMedians.Length == 0 ? null : supported
                    .SelectMany(minute => minute.Loads).Max(),
                MedianLoad = loadMedians.Length == 0 ? null : Quantile(loadMedians, .5),
                P10Load = loadMedians.Length == 0 ? null : Quantile(loadMedians, .1),
                P90Load = loadMedians.Length == 0 ? null : Quantile(loadMedians, .9),
                Reason = reason,
                Unverified = unverified,
            };
        }
    }

    sealed class MinuteBuilder
    {
        public DateTimeOffset Start { get; }
        public List<double> Drops { get; } = new();
        public List<double> Loads { get; } = new();
        public List<DateTimeOffset> SampleTimes { get; } = new();
        public bool Unverified { get; private set; }
        public int Count => Drops.Count;

        public MinuteBuilder(DateTimeOffset start) => Start = start;

        public void Add(RawObservation row, double reference)
        {
            Drops.Add((reference - row.Voltage) * 1000.0);
            SampleTimes.Add(row.SensorTime.ToUniversalTime());
            if (IsWattUnit(row.LoadUnit) && row.Power is double load && double.IsFinite(load))
                Loads.Add(load);
            Unverified |= !string.Equals(row.FreshnessKind?.Trim(),
                "VerifiedSourceTimestamp", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(row.AcquisitionHealth?.Trim(), "SENSOR_UNCHARACTERIZED",
                    StringComparison.OrdinalIgnoreCase) ||
                string.Equals(row.Status?.Trim(), "REFERENCE_UNVERIFIED",
                    StringComparison.OrdinalIgnoreCase);
        }
    }
}
