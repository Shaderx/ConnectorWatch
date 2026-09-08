using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace ConnectorWatch;

public sealed class Config
{
    public string GpuUuid { get; set; } = "";
    public double SampleSeconds { get; set; } = 1;
    public int FlushSeconds { get; set; } = 30;
    public string DataDirectory { get; set; } = "data";
    public bool DesktopAlerts { get; set; } = true;
    // auto prefers a configured external source, then the direct Windows rail
    // provider. Explicit values are direct/nvapi, hwinfo, json, or none.
    public string VoltageSource { get; set; } = "auto";
    public string HwinfoCsv { get; set; } = "";
    // Newline-delimited JSON from an external, independently validated rail reader.
    // ConnectorWatch does not interpret this as proof of a direct NVIDIA source.
    public string RailJson { get; set; } = "";
    public string VoltageColumn { get; set; } = "";
    public string PowerColumn { get; set; } = "";
    // Empty preserves the pre-vNext provider-specific basis at startup. Once
    // resolved, the basis is pinned for the process lifetime and never falls
    // back when a selected measurement disappears.
    public string AnalysisLoadSource { get; set; } = "";
    public string[] ExtraVoltageColumns { get; set; } = [];
    public string DateColumn { get; set; } = "Date";
    public string TimeColumn { get; set; } = "Time";
    public string TimestampFormat { get; set; } = "d.M.yyyy H:mm:ss.fff";
    public string Culture { get; set; } = "en-US";
    public string Delimiter { get; set; } = ",";
    public double MaxAgeSeconds { get; set; } = 5;
    public int BinWatts { get; set; } = 25;
    public int MinAnalysisWatts { get; set; } = 100;
    public int StableSamples { get; set; } = 5;
    public int BaselineSamples { get; set; } = 300;
    public int WindowSamples { get; set; } = 60;
    public int WindowMaxAgeSeconds { get; set; } = 1800;
    public double ShiftVolts { get; set; } = .2;
    public double SuddenDroopVolts { get; set; } = .25;
    public double? GrossUnderVoltageV { get; set; }
    public double? GrossOverVoltageV { get; set; }
    public double CoarseConfirmationSeconds { get; set; } = 0;
    public int CoarseConfirmationSamples { get; set; } = 1;
    public double LoadBoundaryHysteresisWatts { get; set; } = 2.5;
    public int SustainSamples { get; set; } = 5;
    public void Validate()
    {
        if (!double.IsFinite(SampleSeconds) || !double.IsFinite(MaxAgeSeconds) ||
            !double.IsFinite(ShiftVolts) || !double.IsFinite(SuddenDroopVolts) ||
            GrossUnderVoltageV is double grossUnder && !double.IsFinite(grossUnder) ||
            GrossOverVoltageV is double grossOver && !double.IsFinite(grossOver) ||
            !double.IsFinite(CoarseConfirmationSeconds) || !double.IsFinite(LoadBoundaryHysteresisWatts) ||
            FlushSeconds < 1 || FlushSeconds > 60 || SampleSeconds < .2 || SampleSeconds > 60 || BinWatts < 1 || BinWatts > 100 ||
            MinAnalysisWatts < 0 || StableSamples < 1 || BaselineSamples < 5 || BaselineSamples > 10000 ||
            WindowSamples < 3 || WindowSamples > 3600 || WindowMaxAgeSeconds < 1 ||
            ShiftVolts <= 0 || SuddenDroopVolts <= 0 || SustainSamples < 1 ||
            CoarseConfirmationSeconds < 0 || CoarseConfirmationSamples < 1 ||
            LoadBoundaryHysteresisWatts < 0 || LoadBoundaryHysteresisWatts >= BinWatts ||
            GrossUnderVoltageV.HasValue && GrossOverVoltageV.HasValue && GrossUnderVoltageV > GrossOverVoltageV ||
            MaxAgeSeconds < 1 || Delimiter.Length != 1 ||
            (HwinfoCsv.Length > 0 && RailJson.Length > 0))
            throw new Exception("Invalid configuration; check numeric ranges and GPU UUID.");
        if (!new[] { "auto", "direct", "nvapi", "hwinfo", "json", "none" }
                .Contains(VoltageSource, StringComparer.OrdinalIgnoreCase))
            throw new Exception("VoltageSource must be auto, direct, nvapi, hwinfo, json, or none.");
        if (!string.IsNullOrWhiteSpace(AnalysisLoadSource))
            _ = AnalysisLoadSourceExtensions.Parse(AnalysisLoadSource);
        _ = CultureInfo.GetCultureInfo(Culture);
    }
}

public sealed record Gpu(double? Power, double? Temperature, double? Utilization, double? Limit);
public sealed class Nvml : IDisposable
{
    static Nvml()
    {
        // NVIDIA ships nvml.dll on Windows and libnvidia-ml.so.1 on Linux. Keep
        // the managed surface identical while allowing the platform loader to
        // resolve the driver library on either OS.
        NativeLibrary.SetDllImportResolver(typeof(Nvml).Assembly, ResolveLibrary);
    }

    static IntPtr ResolveLibrary(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (!string.Equals(libraryName, "nvml.dll", StringComparison.OrdinalIgnoreCase))
            return IntPtr.Zero;

        string[] candidates =
            OperatingSystem.IsWindows() ? ["nvml.dll"] :
            OperatingSystem.IsLinux() ? ["libnvidia-ml.so.1", "libnvidia-ml.so"] :
            [];
        foreach (var candidate in candidates)
        {
            if (NativeLibrary.TryLoad(candidate, assembly, searchPath, out var handle))
                return handle;
        }
        return IntPtr.Zero;
    }

    [DllImport("nvml.dll", CallingConvention = CallingConvention.Cdecl)] static extern int nvmlInit_v2();
    [DllImport("nvml.dll", CallingConvention = CallingConvention.Cdecl)] static extern int nvmlShutdown();
    [DllImport("nvml.dll", CallingConvention = CallingConvention.Cdecl)] static extern int nvmlDeviceGetCount_v2(out uint count);
    [DllImport("nvml.dll", CallingConvention = CallingConvention.Cdecl)] static extern int nvmlDeviceGetHandleByIndex_v2(uint index, out IntPtr device);
    [DllImport("nvml.dll", CallingConvention = CallingConvention.Cdecl)] static extern int nvmlDeviceGetUUID(IntPtr device, [Out] StringBuilder uuid, uint length);

    internal static string ResolveUuid(string? configured, Func<string>? detect = null) =>
        string.IsNullOrWhiteSpace(configured) || configured.Trim().Equals("auto", StringComparison.OrdinalIgnoreCase)
            ? (detect ?? DetectSingleGpuUuid)() : configured.Trim();

    internal static string SelectAutoUuid(uint count, Func<string> readUuid)
    {
        if (count != 1)
            throw new Exception($"GPU UUID auto-detection requires exactly one NVIDIA GPU; found {count}. Set GpuUuid explicitly for a supported external source. Direct rails do not support multi-GPU systems.");
        var uuid = readUuid().Trim();
        if (!uuid.StartsWith("GPU-", StringComparison.Ordinal) || !Guid.TryParseExact(uuid[4..], "D", out _))
            throw new Exception("NVML returned an invalid physical GPU UUID.");
        return uuid;
    }

    static string DetectSingleGpuUuid()
    {
        if (nvmlInit_v2() != 0) throw new Exception("NVML initialization failed during GPU auto-detection.");
        try
        {
            if (nvmlDeviceGetCount_v2(out var count) != 0) throw new Exception("NVML GPU enumeration failed.");
            return SelectAutoUuid(count, () =>
            {
                if (nvmlDeviceGetHandleByIndex_v2(0, out var handle) != 0 || handle == IntPtr.Zero)
                    throw new Exception("NVML could not open the detected GPU.");
                var uuid = new StringBuilder(96);
                if (nvmlDeviceGetUUID(handle, uuid, (uint)uuid.Capacity) != 0)
                    throw new Exception("NVML could not read the detected GPU UUID.");
                return uuid.ToString();
            });
        }
        finally { nvmlShutdown(); }
    }
    [DllImport("nvml.dll", CallingConvention = CallingConvention.Cdecl)] static extern int nvmlDeviceGetHandleByUUID([MarshalAs(UnmanagedType.LPStr)] string uuid, out IntPtr device);
    [DllImport("nvml.dll", CallingConvention = CallingConvention.Cdecl)] static extern int nvmlDeviceGetPowerUsage(IntPtr device, out uint value);
    [DllImport("nvml.dll", CallingConvention = CallingConvention.Cdecl)] static extern int nvmlDeviceGetPowerManagementLimit(IntPtr device, out uint value);
    [DllImport("nvml.dll", CallingConvention = CallingConvention.Cdecl)] static extern int nvmlDeviceGetTemperature(IntPtr device, uint sensor, out uint value);
    [StructLayout(LayoutKind.Sequential)] struct Util { public uint Gpu, Memory; }
    [DllImport("nvml.dll", CallingConvention = CallingConvention.Cdecl)] static extern int nvmlDeviceGetUtilizationRates(IntPtr device, out Util value);
    readonly IntPtr device;
    bool initialized;
    bool disposed;
    public Nvml(string uuid)
    {
        try
        {
            if (nvmlInit_v2() != 0) throw new Exception("NVML initialization failed.");
            initialized = true;
            if (nvmlDeviceGetHandleByUUID(uuid, out device) != 0)
            {
                nvmlShutdown();
                initialized = false;
                throw new Exception("Configured GPU UUID unavailable.");
            }
        }
        catch (DllNotFoundException ex)
        {
            throw new Exception(
                OperatingSystem.IsLinux()
                    ? "NVML library not found. Install the NVIDIA driver with libnvidia-ml.so.1 available."
                    : "NVML library not found. Install an NVIDIA driver that provides nvml.dll.", ex);
        }
    }
    public Gpu Read() => new(
        nvmlDeviceGetPowerUsage(device, out var p) == 0 ? p / 1000.0 : null,
        nvmlDeviceGetTemperature(device, 0, out var t) == 0 ? t : null,
        nvmlDeviceGetUtilizationRates(device, out var u) == 0 ? u.Gpu : null,
        nvmlDeviceGetPowerManagementLimit(device, out var l) == 0 ? l / 1000.0 : null);
    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        if (initialized) nvmlShutdown();
    }
}

public static class Csv
{
    public static string[] Parse(string line, char delimiter = ',')
    {
        var fields = new List<string>(); var b = new StringBuilder(); bool quoted = false;
        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (c == '"')
            {
                if (quoted && i + 1 < line.Length && line[i + 1] == '"') { b.Append('"'); i++; }
                else quoted = !quoted;
            }
            else if (c == delimiter && !quoted) { fields.Add(b.ToString()); b.Clear(); }
            else b.Append(c);
        }
        if (quoted) throw new FormatException("Incomplete quoted CSV field.");
        fields.Add(b.ToString()); return fields.ToArray();
    }
    public static string Line(params object?[] values) => string.Join(",", values.Select(v =>
        "\"" + (Convert.ToString(v, CultureInfo.InvariantCulture) ?? "").Replace("\"", "\"\"") + "\""));
}

public sealed record Voltage(DateTimeOffset Timestamp, double Volts, double? Power, string Extras);
public interface IVoltageSource
{
    string Description { get; }
    bool TerminalOnFailure => false;
    Voltage Read(DateTimeOffset now);
}

public sealed class HwinfoLog(Config c) : IVoltageSource
{
    public string Description => "HWiNFO CSV: " + c.VoltageColumn;

    // Read bounded head/tail segments, never rescan a growing log. Only complete rows count.
    public static (string Header, string Row) ReadEnds(string path)
    {
        using var f = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        int size = (int)Math.Min(f.Length, 131072);
        var head = new byte[size]; f.ReadExactly(head);
        string h = Encoding.UTF8.GetString(head).TrimStart('\uFEFF');
        int e = h.IndexOf('\n'); if (e < 0) throw new IOException("CSV header incomplete or too large.");
        f.Seek(-size, SeekOrigin.End); var tail = new byte[size]; f.ReadExactly(tail);
        string t = Encoding.UTF8.GetString(tail); int last = t.LastIndexOf('\n');
        if (last < 0) throw new IOException("CSV row incomplete.");
        int prev = t.LastIndexOf('\n', Math.Max(0, last - 1));
        if (prev < 0) throw new IOException("No complete data row.");
        return (h[..e].TrimEnd('\r'), t[(prev + 1)..last].TrimEnd('\r'));
    }
    public Voltage Read(DateTimeOffset now)
    {
        var (h, row) = ReadEnds(c.HwinfoCsv);
        var columns = Csv.Parse(h, c.Delimiter[0]); var values = Csv.Parse(row, c.Delimiter[0]);
        if (columns.Length != values.Length) throw new FormatException("CSV column count changed.");
        string Get(string name)
        {
            var matches = columns.Select((s, i) => (s, i)).Where(x => x.s == name).ToArray();
            if (matches.Length != 1) throw new FormatException("Missing or ambiguous column: " + name);
            return values[matches[0].i];
        }
        var culture = CultureInfo.GetCultureInfo(c.Culture);
        var local = DateTime.ParseExact(Get(c.DateColumn) + " " + Get(c.TimeColumn), c.TimestampFormat, culture, DateTimeStyles.None);
        if (TimeZoneInfo.Local.IsAmbiguousTime(local) || TimeZoneInfo.Local.IsInvalidTime(local)) throw new FormatException("Ambiguous local timestamp.");
        var timestamp = new DateTimeOffset(DateTime.SpecifyKind(local, DateTimeKind.Local)).ToUniversalTime();
        if ((now - timestamp).TotalSeconds > c.MaxAgeSeconds || timestamp > now.AddSeconds(1)) throw new IOException("Stale or future HWiNFO timestamp.");
        double Number(string name)
        {
            var v = double.Parse(Get(name), NumberStyles.Float, culture);
            if (!double.IsFinite(v)) throw new FormatException("Non-finite sensor value."); return v;
        }
        var voltage = Number(c.VoltageColumn);
        // Plausibility only: never interpret core voltage as the 12 V input rail.
        if (voltage < 6 || voltage > 16) throw new FormatException("Selected voltage is outside input-rail plausibility bounds.");
        double? power = string.IsNullOrEmpty(c.PowerColumn) ? null : Number(c.PowerColumn);
        var extras = c.ExtraVoltageColumns.ToDictionary(x => x, Number);
        return new(timestamp, voltage, power, JsonSerializer.Serialize(extras));
    }
}

/// <summary>
/// Reads newline-delimited JSON produced by an external rail reader. The file
/// is an integration boundary: ConnectorWatch does not claim that the writer
/// used a direct NVIDIA or board-level sensor path.
///
/// Record format:
/// {"timestamp_utc":"2026-09-07T12:00:00Z","input_voltage_v":12.1,
///  "power_w":440,"extra_voltages":{"pcie_12v":12.0}}
/// </summary>
public sealed class RailJsonLog(Config c) : IVoltageSource
{
    public string Description => "timestamped rail JSON";

    static string ReadLatestCompleteLine(string path)
    {
        const int BufferSize = 131072;
        using var f = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        long length = f.Length;
        if (length == 0) throw new IOException("JSON rail input is empty.");

        // Snapshot one bounded window from the tail. The byte immediately
        // before a nonzero offset distinguishes an exact record boundary from
        // an offset in the middle of an older record.
        long offset = Math.Max(0, length - BufferSize);
        int size = (int)Math.Min(length, BufferSize);
        bool startsAtRecordBoundary = offset == 0;
        if (!startsAtRecordBoundary)
        {
            f.Position = offset - 1;
            startsAtRecordBoundary = f.ReadByte() == '\n';
        }

        f.Position = offset;
        var bytes = new byte[size];
        int read = 0;
        while (read < bytes.Length)
        {
            int count = f.Read(bytes, read, bytes.Length - read);
            if (count == 0) break;
            read += count;
        }
        if (read == 0) throw new IOException("JSON rail input has no complete record.");

        ReadOnlySpan<byte> data = bytes.AsSpan(0, read);
        int contentStart = 0;
        if (!startsAtRecordBoundary)
        {
            int firstNewline = data.IndexOf((byte)'\n');
            if (firstNewline < 0)
                throw new IOException("JSON rail input has no complete record.");
            contentStart = firstNewline + 1;
        }

        // A line is complete only when its LF delimiter is present. Thus an
        // unterminated final append is ignored. Empty lines are skipped as
        // they are not NDJSON records.
        int newline = data.LastIndexOf((byte)'\n');
        while (newline >= contentStart)
        {
            int lineStart = data[..newline].LastIndexOf((byte)'\n') + 1;
            if (lineStart < contentStart) lineStart = contentStart;
            int lineEnd = newline;
            if (lineEnd > lineStart && data[lineEnd - 1] == '\r') lineEnd--;
            if (lineEnd > lineStart)
            {
                var line = Encoding.UTF8.GetString(data[lineStart..lineEnd]);
                if (offset == 0 && lineStart == 0) line = line.TrimStart('\uFEFF');
                if (line.Length > 0) return line;
            }
            newline = data[..newline].LastIndexOf((byte)'\n');
        }

        throw new IOException("JSON rail input has no complete record.");
    }

    static double Number(JsonElement root, string name, bool required)
    {
        if (!root.TryGetProperty(name, out var value))
        {
            if (required) throw new FormatException("JSON rail record is missing " + name + ".");
            return double.NaN;
        }
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out var result) || !double.IsFinite(result))
            throw new FormatException("JSON rail field is not a finite number: " + name + ".");
        return result;
    }

    public Voltage Read(DateTimeOffset now)
    {
        using var document = JsonDocument.Parse(ReadLatestCompleteLine(c.RailJson));
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object) throw new FormatException("JSON rail record must be an object.");
        if (!root.TryGetProperty("timestamp_utc", out var timestampValue) ||
            timestampValue.ValueKind != JsonValueKind.String ||
            !timestampValue.TryGetDateTimeOffset(out var timestamp))
            throw new FormatException("JSON rail record has an invalid timestamp_utc.");
        timestamp = timestamp.ToUniversalTime();
        if ((now - timestamp).TotalSeconds > c.MaxAgeSeconds || timestamp > now.AddSeconds(1))
            throw new IOException("Stale or future JSON rail timestamp.");

        var voltage = Number(root, "input_voltage_v", required: true);
        if (voltage < 6 || voltage > 16)
            throw new FormatException("JSON input voltage is outside input-rail plausibility bounds.");
        var powerValue = Number(root, "power_w", required: false);
        double? power = double.IsNaN(powerValue) ? null : powerValue;
        var extras = new Dictionary<string, double>(StringComparer.Ordinal);
        if (root.TryGetProperty("extra_voltages", out var extraValue))
        {
            if (extraValue.ValueKind != JsonValueKind.Object)
                throw new FormatException("JSON extra_voltages must be an object.");
            foreach (var property in extraValue.EnumerateObject())
                extras[property.Name] = Number(extraValue, property.Name, required: true);
        }
        return new(timestamp, voltage, power, JsonSerializer.Serialize(extras));
    }
}

public sealed class Bin
{
    public List<double> Learning { get; set; } = [];
    // Reference is the explicitly accepted, frozen value. CandidateReference
    // is learned evidence awaiting operator review; it must never be used for
    // shift detection or be promoted implicitly when the sample count closes.
    public double? Reference { get; set; }
    public double? ReferenceP05 { get; set; }
    public double? CandidateReference { get; set; }
    public double? CandidateReferenceP05 { get; set; }
}
public sealed record Result(string Status, int? Bin, double? Reference, double? Median, double? P05, double? Drop)
{
    public CoarseRailGuardResult? CoarseGuard { get; init; }
    public LoadQualificationResult? LoadQualification { get; init; }
}
public sealed record AnalysisProgress(
    int LearningSamples,
    int BaselineSamples,
    int WindowSamples,
    int WindowTarget,
    int StableSamples,
    int StableTarget);
public sealed class Analysis(Config c, Dictionary<int, Bin>? saved = null)
{
    public Dictionary<int, Bin> Bins { get; } = saved ?? [];
    readonly Dictionary<int, Queue<(DateTimeOffset Time, double V)>> windows = [];
    readonly CoarseRailGuard coarseGuard = new(new CoarseRailGuardOptions
    {
        GrossUnderVoltageV = c.GrossUnderVoltageV,
        GrossOverVoltageV = c.GrossOverVoltageV,
        RapidDiscontinuityVolts = c.SuddenDroopVolts,
        ConfirmationDuration = TimeSpan.FromSeconds(c.CoarseConfirmationSeconds),
        MinimumConfirmationSamples = c.CoarseConfirmationSamples,
        MaximumGapSeconds = c.MaxAgeSeconds,
    });
    readonly HystereticLoadQualifier loadQualifier = new(new LoadQualificationOptions
    {
        MinimumLoadWatts = c.MinAnalysisWatts,
        MaximumLoadWatts = 1000,
        BinWatts = c.BinWatts,
        BoundaryHysteresisWatts = c.LoadBoundaryHysteresisWatts,
        MinimumStableSamples = c.StableSamples,
        MaximumGapSeconds = c.MaxAgeSeconds,
    });
    int sustained;
    public AnalysisProgress Progress { get; private set; } =
        new(0, c.BaselineSamples, 0, c.WindowSamples, 0, c.StableSamples);
    public static double Percentile(IEnumerable<double> values, double p)
    {
        var a = values.Order().ToArray(); double index = (a.Length - 1) * p;
        int lo = (int)index; return a[lo] + (a[(int)Math.Ceiling(index)] - a[lo]) * (index - lo);
    }
    public void Gap()
    {
        coarseGuard.Gap();
        TrendGap();
    }

    /// <summary>
    /// Returns a detached candidate snapshot for the lifecycle owner. An
    /// accepted reference is included as context for already-qualified bins,
    /// but only bins with CandidateReference indicate newly learned evidence.
    /// </summary>
    public ReferenceCandidateModel? BuildCandidate(ReferenceIdentity identity,
        DateTimeOffset observedAtUtc)
    {
        var changed = Bins.Values.Any(b => b.CandidateReference.HasValue);
        if (!changed) return null;

        var bins = Bins
            .Where(pair => pair.Value.Reference.HasValue || pair.Value.CandidateReference.HasValue)
            .ToDictionary(pair => pair.Key, pair =>
                new ReferenceBinStatistics(pair.Key,
                    pair.Value.CandidateReference ?? pair.Value.Reference,
                    pair.Value.CandidateReferenceP05 ?? pair.Value.ReferenceP05,
                    pair.Value.Learning.Count,
                    pair.Value.Learning.Count));
        if (bins.Count == 0) return null;

        var qualified = bins.Values.All(x => x.IsQualified) &&
            Bins.Values.Where(x => x.CandidateReference.HasValue)
                .All(x => x.CandidateReference.HasValue);
        var first = observedAtUtc.AddSeconds(-Math.Max(0, c.BaselineSamples - 1) * c.SampleSeconds);
        return ReferenceCandidateModel.Learned(identity, bins,
            qualifiedSamples: Bins.Values.Sum(x => x.CandidateReference.HasValue
                ? c.BaselineSamples : 0),
            requiredSamples: c.BaselineSamples,
            firstObservedAtUtc: first,
            lastObservedAtUtc: observedAtUtc,
            isQualified: qualified,
            detail: qualified
                ? "A qualified candidate is available; explicit acceptance is still required."
                : "Reference learning is still in progress; no accepted reference was changed.");
    }

    /// <summary>Applies only an explicitly accepted snapshot. The lifecycle
    /// owner calls this after AcceptCandidate succeeds.</summary>
    public void ApplyAccepted(AcceptedReferenceModel accepted)
    {
        if (accepted is null) throw new ArgumentNullException(nameof(accepted));
        foreach (var pair in accepted.Bins)
        {
            if (!Bins.TryGetValue(pair.Key, out var b)) Bins[pair.Key] = b = new();
            b.Reference = pair.Value.ReferenceVolts;
            b.ReferenceP05 = pair.Value.ReferenceP05Volts;
            b.CandidateReference = null;
            b.CandidateReferenceP05 = null;
            b.Learning.Clear();
        }
        windows.Clear();
        sustained = 0;
        Progress = new(0, c.BaselineSamples, 0, c.WindowSamples, 0, c.StableSamples);
    }

    /// <summary>Removes all accepted and candidate values after an explicit
    /// archive, forcing a new qualification cycle.</summary>
    public void ArchiveReferences()
    {
        foreach (var b in Bins.Values)
        {
            b.Reference = null;
            b.ReferenceP05 = null;
            b.CandidateReference = null;
            b.CandidateReferenceP05 = null;
            b.Learning.Clear();
        }
        windows.Clear();
        sustained = 0;
        Progress = new(0, c.BaselineSamples, 0, c.WindowSamples, 0, c.StableSamples);
    }

    void TrendGap()
    {
        loadQualifier.Gap(); sustained = 0; windows.Clear();
        Progress = new(0, c.BaselineSamples, 0, c.WindowSamples, 0, c.StableSamples);
    }

    void SetProgress(int learningSamples, int windowSamples) =>
        Progress = new(learningSamples, c.BaselineSamples, windowSamples, c.WindowSamples,
            loadQualifier.StableSamples, c.StableSamples);

    public Result Add(DateTimeOffset time, double watts, double volts) =>
        Add(time, (double?)watts, volts);

    public Result Add(DateTimeOffset time, double? watts, double volts,
        double? elapsedSeconds = null, bool isFresh = true)
    {
        var coarse = coarseGuard.Observe(time, volts, isFresh, elapsedSeconds);
        if (!coarse.IsValid)
        {
            TrendGap();
            return new("VOLTAGE_UNAVAILABLE", null, null, null, null, null)
                { CoarseGuard = coarse };
        }

        var qualification = loadQualifier.Observe(time, watts, isFresh, elapsedSeconds);
        string? coarseStatus = CoarseStatus(coarse);
        if (coarse.BlocksQualification)
        {
            sustained = 0;
            return new(coarseStatus ?? "COARSE_GUARD_PENDING", qualification.Bin,
                null, null, null, null)
                { CoarseGuard = coarse, LoadQualification = qualification };
        }

        if (qualification.State == LoadQualificationState.Invalid)
        {
            windows.Clear(); sustained = 0;
            string unavailableStatus = watts.HasValue ? "OUTSIDE_ANALYSIS_RANGE" : "ANALYSIS_LOAD_UNAVAILABLE";
            return new(unavailableStatus, null, null, null, null, null)
                { CoarseGuard = coarse, LoadQualification = qualification };
        }
        if (!qualification.IsQualified || !qualification.Bin.HasValue)
        {
            int? settling = qualification.Bin;
            SetProgress(settling.HasValue && Bins.TryGetValue(settling.Value, out var settlingBin)
                ? settlingBin.Learning.Count : 0, 0);
            return new("LOAD_SETTLING", settling, null, null, null, null)
                { CoarseGuard = coarse, LoadQualification = qualification };
        }
        int bin = qualification.Bin.Value;
        if (!Bins.TryGetValue(bin, out var b)) Bins[bin] = b = new();
        if (!windows.TryGetValue(bin, out var q)) windows[bin] = q = new();
        while (q.Count > 0 && (time - q.Peek().Time).TotalSeconds > c.WindowMaxAgeSeconds) q.Dequeue();
        double? priorMedian = q.Count >= c.WindowSamples ? Percentile(q.Select(x => x.V), .5) : null;
        q.Enqueue((time, volts)); while (q.Count > c.WindowSamples) q.Dequeue();
        double median = Percentile(q.Select(x => x.V), .5), p05 = Percentile(q.Select(x => x.V), .05);
        if (!b.Reference.HasValue)
        {
            // A completed candidate remains a candidate until an operator
            // explicitly accepts it. Continue reporting it, but do not use it
            // as a baseline and do not keep mutating the frozen statistics.
            if (!b.CandidateReference.HasValue)
            {
                b.Learning.Add(volts);
                if (b.Learning.Count >= c.BaselineSamples)
                {
                    b.CandidateReference = Percentile(b.Learning, .5);
                    b.CandidateReferenceP05 = Percentile(b.Learning, .05);
                    b.Learning.Clear();
                }
            }
            SetProgress(b.CandidateReference.HasValue ? 0 : b.Learning.Count, q.Count);
            return new(b.CandidateReference.HasValue ? "REFERENCE_UNVERIFIED" : "LEARNING_REFERENCE",
                bin, null, median, p05, null)
                { CoarseGuard = coarse, LoadQualification = qualification };
        }
        double drop = b.Reference.Value - median;
        bool shift = q.Count >= c.WindowSamples && (drop >= c.ShiftVolts || b.ReferenceP05 - p05 >= c.ShiftVolts);
        sustained = shift ? sustained + 1 : 0;
        bool sudden = Math.Max(b.Reference.Value, priorMedian ?? b.Reference.Value) - volts >= c.SuddenDroopVolts;
        string status = sudden ? "SUDDEN_DROOP" : sustained >= c.SustainSamples ? "BASELINE_SHIFT" : q.Count < c.WindowSamples ? "WINDOW_WARMUP" : "NO_SHIFT_DETECTED";
        SetProgress(0, q.Count);
        return new(status, bin, b.Reference, median, p05, drop)
            { CoarseGuard = coarse, LoadQualification = qualification };
    }

    static string? CoarseStatus(CoarseRailGuardResult coarse)
    {
        if (coarse.Absolute.IsConfirmed)
            return coarse.Absolute.Kind == CoarseRailAbsoluteKind.GrossUnderVoltage
                ? "GROSS_UNDERVOLTAGE" : "GROSS_OVERVOLTAGE";
        if (coarse.Discontinuity.IsConfirmed)
            return coarse.Discontinuity.Kind == CoarseRailDiscontinuityKind.RapidDrop
                ? "SUDDEN_DROOP" : "RAPID_VOLTAGE_RISE";
        return coarse.Absolute.State == CoarseRailConditionState.Pending ||
            coarse.Discontinuity.State == CoarseRailConditionState.Pending
            ? "COARSE_GUARD_PENDING" : null;
    }
}

public sealed record Saved(string Identity, Dictionary<int, Bin> Bins);
public static class Program
{
    static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    internal static AnalysisLoadSource ResolveAnalysisLoadSource(Config c, string sourceMode,
        IVoltageSource? voltageSource)
    {
        if (!string.IsNullOrWhiteSpace(c.AnalysisLoadSource))
            return AnalysisLoadSourceExtensions.Parse(c.AnalysisLoadSource);

        // Compatibility for configurations written before the explicit field:
        // resolve once from their configured provider, then pin the result.
        if (voltageSource is DirectNvRails || sourceMode is "direct" or "nvapi")
            return AnalysisLoadSource.CONNECTOR_POWER;
        if (voltageSource is RailJsonLog || voltageSource is HwinfoLog && c.PowerColumn.Length > 0)
            return AnalysisLoadSource.EXTERNAL_SENSOR_POWER;
        return AnalysisLoadSource.NVML_BOARD_POWER;
    }

    internal static ReferenceIdentity BuildReferenceIdentity(Config c, string sourceMode,
        string providerIdentity, AnalysisLoadSource analysisLoadSource)
    {
        bool direct = sourceMode is "direct" or "nvapi" ||
            providerIdentity.StartsWith("direct NVIDIA rails", StringComparison.OrdinalIgnoreCase);
        var qualification = new ReferenceQualificationConfig(
            binWatts: c.BinWatts,
            minAnalysisWatts: c.MinAnalysisWatts,
            stableSamples: c.StableSamples,
            baselineSamples: c.BaselineSamples,
            windowSamples: c.WindowSamples,
            windowMaxAgeSeconds: c.WindowMaxAgeSeconds,
            maxAgeSeconds: c.MaxAgeSeconds,
            sampleSeconds: c.SampleSeconds,
            shiftVolts: c.ShiftVolts,
            suddenDroopVolts: c.SuddenDroopVolts,
            sustainSamples: c.SustainSamples,
            loadBoundaryHysteresisWatts: c.LoadBoundaryHysteresisWatts,
            coarseConfirmationSeconds: c.CoarseConfirmationSeconds,
            coarseConfirmationSamples: c.CoarseConfirmationSamples,
            grossUnderVoltageV: c.GrossUnderVoltageV,
            grossOverVoltageV: c.GrossOverVoltageV);
        return ReferenceIdentity.Create(
            gpuUuid: c.GpuUuid,
            board: direct ? "NVIDIA-target-2B8510DE-89EE1043" : "external-source",
            driver: direct ? DirectNvRails.ExpectedDriverVersion : "external-source",
            source: providerIdentity,
            abiProfile: direct ? "A612-A613-v1" : "external-v1",
            analysisLoadSource: analysisLoadSource,
            featureVersion: "electrical-v1",
            modelVersion: "trend-v1",
            schemaVersion: 3,
            qualification: qualification);
    }

    internal static Dictionary<int, Bin> BuildAnalysisBins(ReferenceLifecycle lifecycle,
        Saved? legacySaved)
    {
        var bins = legacySaved?.Bins is { } oldBins
            ? oldBins.ToDictionary(pair => pair.Key, pair => CloneBin(pair.Value))
            : [];
        foreach (var b in bins.Values)
        {
            // baseline.json is a compatibility mirror. The typed lifecycle is
            // authoritative for deciding whether these values are accepted or
            // merely a candidate, so clear the old interpretation first.
            b.Reference = null;
            b.ReferenceP05 = null;
            b.CandidateReference = null;
            b.CandidateReferenceP05 = null;
        }

        var snapshot = lifecycle.Snapshot();
        if (snapshot.CanAnalyze && snapshot.Accepted is not null)
        {
            foreach (var pair in snapshot.Accepted.Bins)
            {
                if (!bins.TryGetValue(pair.Key, out var b)) bins[pair.Key] = b = new();
                b.Reference = pair.Value.ReferenceVolts;
                b.ReferenceP05 = pair.Value.ReferenceP05Volts;
                b.Learning.Clear();
            }
        }

        // A candidate is safe to display and continue learning against, but it
        // is never passed through the accepted Reference fields.
        if (snapshot.Compatibility != ReferenceCompatibility.MISMATCH &&
            snapshot.Candidate is not null)
        {
            foreach (var pair in snapshot.Candidate.Bins)
            {
                if (!bins.TryGetValue(pair.Key, out var b)) bins[pair.Key] = b = new();
                b.CandidateReference = pair.Value.ReferenceVolts;
                b.CandidateReferenceP05 = pair.Value.ReferenceP05Volts;
                if (b.CandidateReference.HasValue) b.Learning.Clear();
            }
        }
        return bins;
    }

    static Bin CloneBin(Bin source) => new()
    {
        Learning = source.Learning.ToList(),
        Reference = source.Reference,
        ReferenceP05 = source.ReferenceP05,
        CandidateReference = source.CandidateReference,
        CandidateReferenceP05 = source.CandidateReferenceP05,
    };

    public static int Main(string[] args)
    {
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        try
        {
            if (args.Contains("--self-test")) { Tests.Run(); return 0; }
            if (args.Contains("--probe-nvapi")) { NvapiProbe.Run(); return 0; }
            string configPath = Path.GetFullPath(Option(args, "--config") ?? Path.Combine(AppContext.BaseDirectory, "config.json"));
            var c = JsonSerializer.Deserialize<Config>(File.ReadAllText(configPath))!; c.Validate();
            var root = Path.GetDirectoryName(configPath)!;
            if (c.HwinfoCsv.Length > 0) c.HwinfoCsv = Path.GetFullPath(c.HwinfoCsv, root);
            if (c.RailJson.Length > 0) c.RailJson = Path.GetFullPath(c.RailJson, root);
            if (args.Contains("--columns"))
            {
                foreach (var h in Csv.Parse(HwinfoLog.ReadEnds(c.HwinfoCsv).Header, c.Delimiter[0])) Console.WriteLine(h);
                return 0;
            }
            string data = Path.GetFullPath(c.DataDirectory, root); Directory.CreateDirectory(data);
            using var singleInstance = new FileStream(Path.Combine(data, "monitor.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            using var stop = new CancellationTokenSource();
            using var shutdown = RegisterShutdown(stop);
            using var control = new ControlServer(data, stop);
            control.Start();
            try { c.GpuUuid = Nvml.ResolveUuid(c.GpuUuid); }
            catch (Exception ex)
            {
                return WriteStartupFailure(data, "GPU auto-detection", "GPU selection failed: " + ex.Message,
                    control.InstanceId, new AnalysisProgress(0, c.BaselineSamples, 0, c.WindowSamples, 0, c.StableSamples));
            }
            var sourceMode = c.VoltageSource.Trim().ToLowerInvariant();
            DirectNvRails? directSource = null;
            IVoltageSource? voltageSource = null;
            string? sourceSetupError = null;
            if (sourceMode == "none")
            {
                voltageSource = null;
            }
            else if (sourceMode is "direct" or "nvapi")
            {
                try { directSource = new DirectNvRails(c); voltageSource = directSource; }
                catch (Exception ex)
                {
                    return WriteStartupFailure(data, "direct NVIDIA rails", "Direct NVIDIA rail source unavailable: " + ex.Message,
                        control.InstanceId, new AnalysisProgress(0, c.BaselineSamples, 0, c.WindowSamples, 0, c.StableSamples));
                }
            }
            else if (sourceMode == "hwinfo")
            {
                if (c.HwinfoCsv.Length == 0 || c.VoltageColumn.Length == 0)
                    throw new Exception("VoltageSource=hwinfo requires HwinfoCsv and VoltageColumn.");
                voltageSource = new HwinfoLog(c);
            }
            else if (sourceMode == "json")
            {
                if (c.RailJson.Length == 0)
                    throw new Exception("VoltageSource=json requires RailJson.");
                voltageSource = new RailJsonLog(c);
            }
            else
            {
                // Auto uses an explicitly configured external source first. If
                // none is configured, the private rail ABI is attempted only
                // on Windows. A failed native construction is terminal: a
                // timed-out worker may still hold NVAPI state, so do not fall
                // through into another read loop.
                if (c.RailJson.Length > 0)
                {
                    voltageSource = new RailJsonLog(c);
                }
                else if (c.HwinfoCsv.Length > 0 && c.VoltageColumn.Length > 0)
                {
                    voltageSource = new HwinfoLog(c);
                }
                else if (OperatingSystem.IsWindows())
                {
                    try { directSource = new DirectNvRails(c); voltageSource = directSource; }
                    catch (Exception ex)
                    {
                        return WriteStartupFailure(data, "direct NVIDIA rails", "Direct NVIDIA rail source unavailable: " + ex.Message,
                            control.InstanceId, new AnalysisProgress(0, c.BaselineSamples, 0, c.WindowSamples, 0, c.StableSamples));
                    }
                }
                else
                {
                    sourceSetupError = "Direct NVIDIA rail source unavailable on this platform.";
                }
            }
            using var directLifetime = directSource;
            var providerIdentity = voltageSource?.Description ?? (sourceSetupError != null ? "direct NVIDIA rails unavailable" : "none");
            var analysisLoadSource = ResolveAnalysisLoadSource(c, sourceMode, voltageSource);
            var identity = $"v4|{c.GpuUuid}|source={sourceMode}|provider={providerIdentity}|{c.HwinfoCsv}|{c.RailJson}|{c.VoltageColumn}|{c.PowerColumn}|analysis-load={analysisLoadSource.WireName()}|qualification=hysteresis-v1|bin={c.BinWatts}|hysteresis={c.LoadBoundaryHysteresisWatts:R}|stable={c.StableSamples}|baseline={c.BaselineSamples}";
            string baselinePath = Path.Combine(data, "baseline.json");
            string referencePath = Path.Combine(data, "reference.json");
            Saved? saved = null;
            if (File.Exists(baselinePath))
            {
                try { saved = JsonSerializer.Deserialize<Saved>(File.ReadAllText(baselinePath)); }
                catch (JsonException) { /* typed reference.json remains authoritative */ }
            }
            var referenceIdentity = BuildReferenceIdentity(c, sourceMode, providerIdentity, analysisLoadSource);
            bool hasPersistedReference = File.Exists(referencePath) || File.Exists(baselinePath);
            string? persistedReferenceJson = File.Exists(referencePath)
                ? File.ReadAllText(referencePath)
                : File.Exists(baselinePath) ? File.ReadAllText(baselinePath) : null;
            var referenceLoad = ReferencePersistence.Load(persistedReferenceJson, referenceIdentity,
                DateTimeOffset.UtcNow,
                new ReferenceStartupContext(
                    IsRestart: hasPersistedReference,
                    IsDegraded: voltageSource is null || sourceSetupError is not null,
                    Detail: sourceSetupError ?? ""));
            var lifecycle = referenceLoad.Lifecycle;
            var referenceGate = new object();
            var analysis = new Analysis(c, BuildAnalysisBins(lifecycle, saved));

            string BaselineMirror() => JsonSerializer.Serialize(new Saved(identity, analysis.Bins), Json);
            void PersistReferenceLocked()
            {
                Atomic(referencePath, ReferencePersistence.Serialize(lifecycle));
                // Keep the historical shape available to existing GUI builds
                // and package consumers. It is only a mirror; lifecycle state
                // and acceptance metadata live in reference.json.
                Atomic(baselinePath, BaselineMirror());
            }
            ControlCommandResult? HandleReferenceCommand(ControlRequest request)
            {
                lock (referenceGate)
                {
                    string command = request.Command.Trim().ToLowerInvariant();
                    string actor = string.IsNullOrWhiteSpace(request.Operator)
                        ? request.ClientId : request.Operator.Trim();
                    string note = request.Note?.Trim() ?? "";
                    DateTimeOffset now = DateTimeOffset.UtcNow;
                    ReferenceOperationResult operation;
                    if (command is "accept-reference" or "migrate-reference")
                    {
                        operation = lifecycle.AcceptCandidate(now, actor, note,
                            explicitLegacyMigration: command == "migrate-reference" ||
                                request.ExplicitLegacyMigration);
                        if (operation.Succeeded && lifecycle.Accepted is not null)
                        {
                            analysis.ApplyAccepted(lifecycle.Accepted);
                            PersistReferenceLocked();
                        }
                        return new(operation.Succeeded, operation.Detail,
                            lifecycle.State.WireName());
                    }

                    try
                    {
                        _ = lifecycle.ArchiveAccepted(now, actor,
                            ReferenceArchiveReason.EXPLICIT, note);
                        analysis.ArchiveReferences();
                        PersistReferenceLocked();
                        return new(true, lifecycle.Detail, lifecycle.State.WireName());
                    }
                    catch (InvalidOperationException ex)
                    {
                        return new(false, ex.Message, lifecycle.State.WireName());
                    }
                }
            }
            control.ReferenceCommand = HandleReferenceCommand;
            Nvml nvml;
            try { nvml = new Nvml(c.GpuUuid); }
            catch (Exception ex)
            {
                return WriteStartupFailure(data, "NVML", "NVML initialization failed: " + ex.Message,
                    control.InstanceId, analysis.Progress, lifecycle.Snapshot());
            }
            using var nvmlLifetime = nvml;
            int count = int.Parse(Option(args, "--samples") ?? "0"); int n = 0;
            DateTimeOffset? lastSensor = null; string? previousStatus = null;
            bool alertPresentedForCurrentStatus = false;
            var clock = Stopwatch.StartNew(); double next = 0;
            long startedMonotonic = Stopwatch.GetTimestamp();
            var pollTimingTracker = new PollTimingTracker();
            var samplingProgress = new SamplingProgressTracker(control.InstanceId,
                DateTimeOffset.UtcNow, startedMonotonic,
                staleAfterSeconds: Math.Max(c.MaxAgeSeconds, c.SampleSeconds * 3),
                startupGraceSeconds: Math.Max(c.MaxAgeSeconds, c.SampleSeconds * 3));
            var coverageTracker = new AnalysisCoverageTracker(TimeSpan.FromMinutes(30));
            long? priorCompletedPoll = null;
            var storage = new HybridStorage(data, c.FlushSeconds);
            string? latestState = null;
            Console.WriteLine("ConnectorWatch: read-only telemetry. Ctrl+C/SIGTERM stops. No status certifies connector safety.");
            while (!stop.IsCancellationRequested && (count == 0 || n < count))
            {
                var now = DateTimeOffset.UtcNow; var g = nvml.Read(); Voltage? v = null; ElectricalSample? electrical = null;
                long pollStartMonotonic = Stopwatch.GetTimestamp();
                string status = sourceSetupError != null ? "VOLTAGE_UNAVAILABLE" : "VOLTAGE_NOT_CONFIGURED";
                string detail = sourceSetupError ?? "";
                Exception? terminalFailure = null;
                if (voltageSource != null)
                {
                    try
                    {
                        electrical = voltageSource.ReadElectrical(now, c.MaxAgeSeconds);
                        v = electrical.ToLegacyVoltage();
                        status = "WAITING_FOR_FRESH_VOLTAGE";
                    }
                    catch (Exception ex) when (!voltageSource.TerminalOnFailure &&
                        (ex is IOException or FormatException or JsonException or ArgumentException or UnauthorizedAccessException))
                    { status = "VOLTAGE_UNAVAILABLE"; detail = ex.Message; }
                    catch (Exception ex)
                    {
                        status = "VOLTAGE_UNAVAILABLE";
                        detail = ex.Message + "; monitor stopped";
                        terminalFailure = ex;
                    }
                }
                long pollEndMonotonic = Stopwatch.GetTimestamp();
                var pollTiming = pollTimingTracker.Record(pollStartMonotonic, pollEndMonotonic, now,
                    electrical is null ? new RawElectricalObservation() :
                        RawElectricalObservation.FromElectricalSample(electrical));
                samplingProgress.CompleteSample(pollEndMonotonic, now);
                var load = electrical?.SelectAnalysisLoad(analysisLoadSource, g.Power,
                    FreshnessMetadata.HostPoll(now, "NVML board-power timestamp is the daemon poll time."));
                double? analysisPower = electrical != null && load != null
                    ? load.ToAnalysisPowerWatts(electrical)
                    : null;
                Result result;
                ReferenceStatusSnapshot referenceStatus;
                bool referenceChanged;
                AnalysisProgress analysisProgress;
                lock (referenceGate)
                {
                    result = new Result(status, null, null, null, null, null);
                    if (v != null && (!lastSensor.HasValue || v.Timestamp > lastSensor.Value))
                    {
                        result = analysis.Add(v.Timestamp, analysisPower, v.Volts,
                            clock.Elapsed.TotalSeconds, electrical?.IsFresh == true);
                        lastSensor = v.Timestamp;
                        if (result.Status == "ANALYSIS_LOAD_UNAVAILABLE")
                            detail = load?.Detail ?? $"Selected {analysisLoadSource.WireName()} is unavailable.";
                    }
                    else if (v == null)
                    {
                        analysis.Gap();
                    }
                    var beforeState = lifecycle.State;
                    bool beforeCandidateQualified = lifecycle.Candidate?.IsQualified == true;
                    if (v is not null)
                    {
                        var candidate = analysis.BuildCandidate(referenceIdentity, now);
                        // ReferenceLifecycle preserves LEGACY_MIGRATION origin
                        // when refreshing an imported candidate, so sampling
                        // cannot bypass explicit migration acknowledgement.
                        if (candidate is not null)
                            _ = lifecycle.SetCandidate(candidate);
                    }
                    referenceStatus = lifecycle.Snapshot();
                    referenceChanged = beforeState != referenceStatus.State ||
                        beforeCandidateQualified != (referenceStatus.Candidate?.IsQualified == true);
                    analysisProgress = analysis.Progress;
                }
                double sampleDuration = priorCompletedPoll.HasValue
                    ? MonotonicTime.ElapsedSeconds(priorCompletedPoll.Value, pollEndMonotonic) ?? 0
                    : 0;
                priorCompletedPoll = pollEndMonotonic;
                bool loadKnown = analysisPower.HasValue;
                bool loaded = analysisPower >= c.MinAnalysisWatts ||
                    !loadKnown && (g.Power >= c.MinAnalysisWatts || !g.Power.HasValue);
                bool eligible = result.LoadQualification?.IsQualified == true;
                bool analyzed = eligible && result.Reference.HasValue &&
                    result.Status is "NO_SHIFT_DETECTED" or "SUDDEN_DROOP" or "BASELINE_SHIFT";
                var coverageReason = analyzed ? CoverageObservationReason.ANALYZED
                    : !loadKnown ? CoverageObservationReason.UNKNOWN_LOAD
                    : CoverageObservationReasonExtensions.FromDetectorStatus(result.Status);
                coverageTracker.Record(new CoverageObservation(now, loaded, eligible, analyzed,
                    coverageReason, sampleDuration, loadKnown, detail));
                var coverage = coverageTracker.Snapshot(now);
                var progressContract = samplingProgress.Snapshot(pollEndMonotonic, now);
                var detectorAvailability = new Dictionary<string, DetectorAvailability>(StringComparer.Ordinal)
                {
                    ["coarse"] = result.CoarseGuard?.IsValid == true
                        ? DetectorAvailability.Ready("coarse", now, detail: result.CoarseGuard.Detail)
                        : DetectorAvailability.Unavailable("coarse",
                            electrical is null ? DetectorAvailabilityReason.SOURCE_GAP : DetectorAvailabilityReason.STALE,
                            now, detail: detail),
                    ["legacy_trend"] = DetectorAvailability.FromStatus("legacy_trend", result.Status,
                        now, analysisProgress.WindowSamples, c.WindowSamples, detail),
                };
                bool timestampValid = electrical is not null &&
                    electrical.Freshness.Kind != FreshnessKind.Unavailable &&
                    (!electrical.Freshness.AgeSeconds.HasValue ||
                     electrical.Freshness.AgeSeconds is double sampleAge && sampleAge >= 0 && double.IsFinite(sampleAge));
                var acquisition = AcquisitionHealth.Evaluate(new AcquisitionHealthInput(
                    MonitorRunning: terminalFailure is null,
                    SourceConfigured: voltageSource is not null,
                    SourceAvailable: electrical?.Connector.HasVoltage == true,
                    TimestampValid: timestampValid,
                    Fresh: electrical?.Freshness.IsFresh == true,
                    PowerAvailable: analysisPower.HasValue,
                    NativeFailure: terminalFailure is not null && voltageSource?.TerminalOnFailure == true,
                    SensorCharacterized: false,
                    AnalysisAvailable: analyzed,
                    HostTimestampUtc: now,
                    SourceTimestampUtc: electrical?.Freshness.SourceTimestampUtc,
                    SampleAgeSeconds: electrical?.Freshness.AgeSeconds,
                    Source: voltageSource?.Description ?? "",
                    Detail: detail,
                    FreshnessVerified: electrical?.Freshness.TimestampVerified == true),
                    detectorAvailability);
                string analysisPowerSource = analysisLoadSource.WireName();
                string row = Csv.Line(now.ToString("O"), c.GpuUuid, g.Power, v?.Volts, v?.Timestamp.ToString("O"), analysisPower,
                    analysisPowerSource, g.Temperature, g.Utilization, g.Limit,
                    v != null ? voltageSource?.Description : null, v?.Extras, result.Bin, result.Reference, result.Median, result.P05, result.Drop, result.Status, detail,
                    electrical?.Connector.CurrentA, electrical?.Connector.PowerW, electrical?.Pcie.VoltageV,
                    electrical?.Pcie.CurrentA, electrical?.Pcie.PowerW, electrical?.Source,
                    electrical?.Freshness.Kind, electrical?.Freshness.SourceTimestampUtc?.ToString("O"),
                    electrical?.Connector.PowerProvenance, load?.Unit,
                    acquisition.Status, pollTiming.PollStartMonotonic, pollTiming.PollEndMonotonic,
                    pollTiming.LatencySeconds, pollTiming.ValueChangeFlags,
                    pollTiming.ConsecutiveIdenticalObservations, coverage.CoveragePercent,
                    coverage.EligibleLoadedCount, coverage.AnalyzedLoadedCount,
                    coverage.CurrentUnanalyzedLoadedSeconds, coverage.LongestUnanalyzedLoadedSeconds,
                    progressContract.LastCompletedSampleMonotonic, progressContract.SampleAgeSeconds) + "\n";
                storage.Add(now, row);
                var progress = analysisProgress;
                var state = new
                {
                    schema_version = 3,
                    gpu_uuid = c.GpuUuid,
                    process_id = control.ProcessId,
                    instance_id = control.InstanceId,
                    timestamp_utc = now,
                    gpu = g,
                    voltage = v,
                    voltage_source = voltageSource?.Description,
                    electrical,
                    analysis_load = load,
                    analysis = result,
                    reference_lifecycle = referenceStatus,
                    acquisition,
                    coverage,
                    poll_timing = pollTiming,
                    sampling_progress = progressContract,
                    progress = new
                    {
                        learning_samples = progress.LearningSamples,
                        baseline_samples = progress.BaselineSamples,
                        window_samples = progress.WindowSamples,
                        window_target = progress.WindowTarget,
                        stable_samples = progress.StableSamples,
                        stable_target = progress.StableTarget,
                    },
                    detail,
                    stopped = terminalFailure != null,
                };
                latestState = JsonSerializer.Serialize(state, Json);
                control.Publish(latestState, row);
                bool urgent = referenceChanged || result.Status != previousStatus && result.Status is "SUDDEN_DROOP" or "BASELINE_SHIFT" or "VOLTAGE_UNAVAILABLE" or "POWER_UNAVAILABLE" or "ANALYSIS_LOAD_UNAVAILABLE";

                bool suppressAlerts = control.AlertsSuppressed;
                if (result.Status != previousStatus)
                {
                    Console.WriteLine($"{now:O} {result.Status} | {g.Power:F1} W | {v?.Volts:F3} V | {detail}");
                    storage.AddEvent(Csv.Line(now.ToString("O"), result.Status, result.Drop, detail) + "\n");
                    alertPresentedForCurrentStatus = c.DesktopAlerts && DesktopAlerts.ShouldNotify(result.Status) && !suppressAlerts;
                    if (alertPresentedForCurrentStatus)
                        DesktopAlerts.Notify(result.Status, v?.Volts, detail);
                    previousStatus = result.Status;
                }
                else if (c.DesktopAlerts && !suppressAlerts && !alertPresentedForCurrentStatus &&
                    DesktopAlerts.ShouldNotify(result.Status))
                {
                    // A GUI may have owned presentation when this incident began
                    // and then disappeared. Once its lease expires, restore one
                    // headless notification for the still-active status.
                    DesktopAlerts.Notify(result.Status, v?.Volts, detail);
                    alertPresentedForCurrentStatus = true;
                }
                if (storage.Due(clock.Elapsed.TotalSeconds) || urgent || terminalFailure != null)
                {
                    lock (referenceGate)
                    {
                        PersistReferenceLocked();
                        storage.Flush(clock.Elapsed.TotalSeconds, latestState, BaselineMirror());
                    }
                }
                if (terminalFailure != null)
                {
                    lock (referenceGate) PersistReferenceLocked();
                    throw new Exception("ConnectorWatch stopped after a terminal voltage-source failure.", terminalFailure);
                }
                n++;
                next += c.SampleSeconds;
                if (next < clock.Elapsed.TotalSeconds) next = clock.Elapsed.TotalSeconds;
                while (!stop.IsCancellationRequested && clock.Elapsed.TotalSeconds < next)
                {
                    int milliseconds = (int)Math.Clamp((next - clock.Elapsed.TotalSeconds) * 1000, 1, 100);
                    if (stop.Token.WaitHandle.WaitOne(milliseconds)) break;
                }
            }
            lock (referenceGate)
            {
                if (latestState != null) storage.Flush(clock.Elapsed.TotalSeconds, latestState, BaselineMirror());
                PersistReferenceLocked();
            }
            samplingProgress.MarkStopped(control.StopRequested ? "control" : stop.IsCancellationRequested ? "signal" : "sample_limit");
            MarkStopped(Path.Combine(data, "status.json"), control.StopRequested ? "control" : stop.IsCancellationRequested ? "signal" : "sample_limit",
                control, analysis.Progress, samplingProgress.Snapshot(Stopwatch.GetTimestamp(), DateTimeOffset.UtcNow));
            lock (referenceGate) PersistReferenceLocked();
            if (stop.IsCancellationRequested) Console.WriteLine("ConnectorWatch: shutdown requested; state saved.");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("ConnectorWatch stopped: " + ex.Message);
            try
            {
                string logs = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ConnectorWatch", "logs");
                Directory.CreateDirectory(logs);
                string log = Path.Combine(logs, "errors.log");
                if (File.Exists(log) && new FileInfo(log).Length > 1024 * 1024) File.Move(log, log + ".previous", true);
                File.AppendAllText(log, $"{DateTimeOffset.UtcNow:O} {ex}\n");
            }
            catch (Exception logError) { Console.Error.WriteLine("Unable to save diagnostic: " + logError.Message); }
            return 1;
        }
    }
    static string? Option(string[] args, string name) { int i = Array.IndexOf(args, name); return i < 0 ? null : i + 1 < args.Length ? args[i + 1] : throw new Exception("Missing value for " + name); }
    static void Atomic(string path, string value) => HybridStorage.Atomic(path, value);

    static void MarkStopped(string path, string reason, ControlServer? control = null,
        AnalysisProgress? progress = null, SamplingProgressContract? samplingProgress = null)
    {
        try
        {
            JsonObject state;
            if (File.Exists(path) && JsonNode.Parse(File.ReadAllText(path)) is JsonObject existing)
                state = existing;
            else
                state = new JsonObject
                {
                    ["timestamp_utc"] = DateTimeOffset.UtcNow,
                    ["gpu"] = null,
                    ["voltage"] = null,
                    ["electrical"] = null,
                    ["analysis_load"] = null,
                    ["analysis"] = null,
                    ["detail"] = "Monitor stopped before the first sample.",
                };
            state["schema_version"] = 3;
            state["process_id"] = control?.ProcessId ?? Environment.ProcessId;
            state["instance_id"] = control?.InstanceId ?? Guid.NewGuid().ToString("N");
            if (progress is not null)
            {
                state["progress"] = new JsonObject
                {
                    ["learning_samples"] = progress.LearningSamples,
                    ["baseline_samples"] = progress.BaselineSamples,
                    ["window_samples"] = progress.WindowSamples,
                    ["window_target"] = progress.WindowTarget,
                    ["stable_samples"] = progress.StableSamples,
                    ["stable_target"] = progress.StableTarget,
                };
            }
            if (samplingProgress is not null)
                state["sampling_progress"] = JsonSerializer.SerializeToNode(samplingProgress, Json);
            state["stopped"] = true;
            state["stop_reason"] = reason;
            state["stopped_at_utc"] = DateTimeOffset.UtcNow;
            Atomic(path, state.ToJsonString(Json));
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("ConnectorWatch could not mark stopped state: " + ex.Message);
        }
    }

    static int WriteStartupFailure(string data, string source, string detail, string? instanceId = null,
        AnalysisProgress? progress = null, ReferenceStatusSnapshot? referenceLifecycle = null)
    {
        var now = DateTimeOffset.UtcNow;
        var result = new Result("VOLTAGE_UNAVAILABLE", null, null, null, null, null);
        var currentProgress = progress ?? new AnalysisProgress(0, 0, 0, 0, 0, 0);
        try
        {
            var state = new
            {
                schema_version = 3,
                process_id = Environment.ProcessId,
                instance_id = instanceId ?? Guid.NewGuid().ToString("N"),
                timestamp_utc = now,
                gpu = new Gpu(null, null, null, null),
                voltage = (Voltage?)null,
                voltage_source = source,
                electrical = (ElectricalSample?)null,
                analysis_load = (AnalysisLoadSelection?)null,
                analysis = result,
                reference_lifecycle = referenceLifecycle,
                progress = new
                {
                    learning_samples = currentProgress.LearningSamples,
                    baseline_samples = currentProgress.BaselineSamples,
                    window_samples = currentProgress.WindowSamples,
                    window_target = currentProgress.WindowTarget,
                    stable_samples = currentProgress.StableSamples,
                    stable_target = currentProgress.StableTarget,
                },
                detail,
                stopped = true,
            };
            Atomic(Path.Combine(data, "status.json"), JsonSerializer.Serialize(state, Json));
            File.AppendAllText(Path.Combine(data, "events.csv"), Csv.Line(now.ToString("O"), result.Status, null, detail) + "\n");
        }
        catch (Exception persist)
        {
            Console.Error.WriteLine("ConnectorWatch could not persist startup failure: " + persist.Message);
        }
        Console.Error.WriteLine("ConnectorWatch stopped: " + detail);
        return 1;
    }

    static IDisposable RegisterShutdown(CancellationTokenSource stop)
    {
        ConsoleCancelEventHandler console = (_, e) => { e.Cancel = true; stop.Cancel(); };
        Console.CancelKeyPress += console;
        PosixSignalRegistration? term = null;
        if (!OperatingSystem.IsWindows())
            term = PosixSignalRegistration.Create(PosixSignal.SIGTERM, context => { context.Cancel = true; stop.Cancel(); });
        return new ShutdownRegistration(console, term);
    }

    sealed class ShutdownRegistration(ConsoleCancelEventHandler console, PosixSignalRegistration? term) : IDisposable
    {
        public void Dispose()
        {
            Console.CancelKeyPress -= console;
            term?.Dispose();
        }
    }
}
