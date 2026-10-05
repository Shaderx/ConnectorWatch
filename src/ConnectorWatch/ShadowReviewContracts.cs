using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;

namespace ConnectorWatch;

/// <summary>Versioned, disk-only state shared by the daemon and the GUI.</summary>
public static class ShadowReviewStore
{
    public const int SchemaVersion = 1;
    internal const int MaximumStateBytes = 256 * 1024;
    internal const int MaximumHistoryEntries = 12;
    internal const int MaximumRetainedReports = 12;

    public static string DirectoryFor(string dataDirectory)
    {
        var normalized = ControlEndpoint.NormalizeDataDirectory(dataDirectory);
        return DirectoryFor(normalized, Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
    }

    public static string StatePath(string dataDirectory) => Path.Combine(DirectoryFor(dataDirectory), "state.json");

    public static ShadowReviewSnapshot? Read(string dataDirectory, out string? error)
    {
        error = null;
        try
        {
            string normalized = ControlEndpoint.NormalizeDataDirectory(dataDirectory);
            return ReadAtPath(normalized, StatePath(normalized), out error);
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return null;
        }
    }

    internal static ShadowReviewSnapshot? ReadAtPath(string dataDirectory, string path, out string? error)
    {
        error = null;
        try
        {
            string normalized = ControlEndpoint.NormalizeDataDirectory(dataDirectory);
            if (!File.Exists(path)) return null;

            byte[] bytes = ReadBounded(path, MaximumStateBytes);
            using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 32,
            });
            var root = document.RootElement;
            RequireObject(root, "snapshot");
            RequireProperties(root, "SchemaVersion", "DataDirectory", "Status", "Detail", "UpdatedUtc",
                "StartedUtc", "NextAttemptUtc", "LastSuccessUtc", "ActiveRunId", "History");
            ValidateOptionalRecoveryProperties(root);

            var snapshot = JsonSerializer.Deserialize<ShadowReviewSnapshot>(bytes, JsonOptions)
                ?? throw new InvalidDataException("State file did not contain a snapshot.");
            Validate(snapshot, normalized, root);
            return snapshot;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return null;
        }
    }

    internal static string DirectoryFor(string normalizedDataDirectory, string localAppDataRoot)
    {
        if (string.IsNullOrWhiteSpace(localAppDataRoot))
            throw new InvalidOperationException("The local application data directory is not available.");

        string hashInput = OperatingSystem.IsWindows()
            ? normalizedDataDirectory.ToUpperInvariant()
            : normalizedDataDirectory;
        string digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(hashInput)));
        return Path.Combine(localAppDataRoot, "ConnectorWatch", "shadow-reviews", digest);
    }

    internal static ShadowReviewSnapshot WriteAtomic(string path, ShadowReviewSnapshot snapshot)
    {
        if (snapshot.Recovery is null || snapshot.RetainedReports is null || snapshot.History is null ||
            snapshot.RetainedReports.Any(item => item is null))
            throw new InvalidDataException("State snapshot contains a null recovery field or entry.");
        string normalizedDataDirectory = ControlEndpoint.NormalizeDataDirectory(snapshot.DataDirectory);
        var compacted = Compact(snapshot);
        string directory = Path.GetDirectoryName(path)
            ?? throw new InvalidOperationException("A state directory is required.");
        Directory.CreateDirectory(directory);
        while (true)
        {
            Validate(compacted, normalizedDataDirectory, null);
            string temporary = Path.Combine(directory, ".state-" + Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                           16 * 1024, FileOptions.WriteThrough))
                {
                    using (var bounded = new BoundedWriteStream(stream, MaximumStateBytes))
                        JsonSerializer.Serialize(bounded, compacted, JsonOptions);
                    stream.Flush(flushToDisk: true);
                }

                for (int attempt = 0; ; attempt++)
                {
                    try
                    {
                        File.Move(temporary, path, overwrite: true);
                        break;
                    }
                    catch (Exception ex) when (IsRetryableReplacement(ex) && attempt < 4)
                    {
                        Thread.Sleep(25 << attempt);
                    }
                }
                return compacted;
            }
            catch (InvalidDataException ex) when (ex.Message.Contains("write limit", StringComparison.Ordinal) &&
                                                   compacted.Recovery.Detail.Length > 128)
            {
                const string marker = "... Recovery details omitted to keep the state within its size limit.";
                int keep = Math.Max(0, compacted.Recovery.Detail.Length / 2 - marker.Length);
                compacted = compacted with
                {
                    Recovery = compacted.Recovery with
                    {
                        Detail = compacted.Recovery.Detail[..keep] + marker,
                    },
                };
            }
            catch (InvalidDataException ex) when (ex.Message.Contains("write limit", StringComparison.Ordinal) &&
                                                   compacted.RetainedReports.Length > 0)
            {
                string omitted = "Additional retained reports were omitted to keep the state file within its size limit.";
                compacted = compacted with
                {
                    RetainedReports = compacted.RetainedReports.Take(compacted.RetainedReports.Length - 1).ToArray(),
                    Recovery = compacted.Recovery with { Detail = AppendBounded(compacted.Recovery.Detail, omitted, 2048) },
                };
            }
            catch (InvalidDataException ex) when (ex.Message.Contains("write limit", StringComparison.Ordinal) &&
                                                   compacted.History.Length > 1)
            {
                compacted = compacted with { History = compacted.History.Take(compacted.History.Length - 1).ToArray() };
            }
            finally
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }
        }
    }

    static bool IsRetryableReplacement(Exception exception) =>
        exception is UnauthorizedAccessException ||
        exception is IOException && (exception.HResult & 0xffff) is 32 or 33;

    static ShadowReviewSnapshot Compact(ShadowReviewSnapshot snapshot)
    {
        int omittedReports = Math.Max(0, snapshot.RetainedReports.Length - MaximumRetainedReports);
        string recoveryDetail = CompactRecoveryDetail(snapshot.Recovery.Detail ?? string.Empty);
        if (omittedReports > 0)
            recoveryDetail = AppendBounded(recoveryDetail,
                $"{omittedReports} additional retained report(s) were omitted from the display.", 2048);
        return snapshot with
        {
            Detail = CompactStateDetail(snapshot.Detail ?? string.Empty),
            History = (snapshot.History ?? []).Select(CompactSummary).ToArray(),
            RetainedReports = snapshot.RetainedReports.Take(MaximumRetainedReports)
                .Select(item => item with { Detail = CompactText(item.Detail ?? string.Empty, 512) }).ToArray(),
            Recovery = snapshot.Recovery with { Detail = recoveryDetail },
        };
    }

    internal static ShadowReviewSummary CompactSummary(ShadowReviewSummary item)
    {
        const int maximumWarnings = 24;
        const int maximumWarningLength = 512;
        const string marker = "Additional warning details are in the full report.";
        string[] sourceWarnings = item.Warnings ?? [];
        bool clipped = sourceWarnings.Length > maximumWarnings ||
            sourceWarnings.Any(x => (x?.Length ?? 0) > maximumWarningLength);
        bool IsEssential(string? warning) => warning is not null &&
            (warning.StartsWith("Input exclusions:", StringComparison.Ordinal) ||
             warning.StartsWith("The newest selected-cohort test day", StringComparison.Ordinal) ||
             warning.StartsWith("This review completed without new source or selected-cohort heldout rows", StringComparison.Ordinal));
        var essential = sourceWarnings.Where(IsEssential).Distinct(StringComparer.Ordinal).ToArray();
        var other = sourceWarnings.Where(x => !IsEssential(x)).ToArray();
        int room = Math.Max(0, maximumWarnings - essential.Length - (clipped ? 1 : 0));
        var warnings = essential.Concat(other.Take(room))
            .Select(x => CompactText(x ?? string.Empty, maximumWarningLength)).ToList();
        if (clipped && !warnings.Contains(marker, StringComparer.Ordinal)) warnings.Add(marker);

        return item with
        {
            Conclusion = CompactText(item.Conclusion ?? string.Empty, 2048),
            Warnings = warnings.ToArray(),
            Models = (item.Models ?? []).Select(model => model with
                { Detail = CompactText(model.Detail ?? string.Empty, 512) }).ToArray(),
            SyntheticFaults = (item.SyntheticFaults ?? []).Select(fault => fault with
                { Detail = CompactText(fault.Detail ?? string.Empty, 512) }).ToArray(),
        };
    }

    internal static ShadowReviewSummary PrepareCompletionSummary(
        ShadowReviewSummary summary, string dataDirectory)
    {
        if (summary is null) throw new ArgumentNullException(nameof(summary));
        string normalized = ControlEndpoint.NormalizeDataDirectory(dataDirectory);
        ShadowReviewSummary compact = CompactSummary(summary);
        ValidateSummary(compact);
        var projection = new ShadowReviewSnapshot
        {
            DataDirectory = normalized,
            Status = "COMPLETED",
            // Reserve the largest permitted serialized metadata, including escaped
            // diagnostics, so a committed summary remains writable during recovery.
            Detail = new string('\u0001', 2048),
            UpdatedUtc = compact.CompletedUtc,
            StartedUtc = compact.InputCutoffUtc.AddTicks(1234567),
            NextAttemptUtc = compact.CompletedUtc + TimeSpan.FromDays(7),
            LastSuccessUtc = compact.CompletedUtc,
            ActiveRunId = new string('x', 128),
            LastAttemptRunId = new string('x', 128),
            History = [compact],
            Recovery = new ShadowReviewRecoveryInfo
            {
                Status = "ATTENTION",
                CheckedUtc = compact.InputCutoffUtc.AddTicks(1234567),
                RecoveredCount = int.MaxValue,
                ScanIncomplete = false,
                Detail = new string('\u0001', 2048),
            },
        };
        projection = Compact(projection);
        Validate(projection, normalized, null);
        using var memory = new MemoryStream();
        using (var bounded = new BoundedWriteStream(memory, MaximumStateBytes))
            JsonSerializer.Serialize(bounded, projection, JsonOptions);
        return projection.History[0];
    }

    static string CompactText(string value, int limit)
    {
        if (value.Length <= limit) return value;
        const string textMarker = "… Additional details are in the full report.";
        return value[..Math.Max(0, limit - textMarker.Length)] + textMarker;
    }

    static string CompactStateDetail(string value)
    {
        const string textMarker = "… Details truncated.";
        if (value.Length <= 2048) return value;
        return value[..(2048 - textMarker.Length)] + textMarker;
    }

    static string CompactRecoveryDetail(string value)
    {
        const string textMarker = "… Recovery details truncated.";
        if (value.Length <= 2048) return value;
        return value[..(2048 - textMarker.Length)] + textMarker;
    }

    static string AppendBounded(string current, string addition, int limit)
    {
        if (current.Contains(addition, StringComparison.Ordinal)) return current;
        string joined = string.IsNullOrWhiteSpace(current) ? addition : current + " " + addition;
        if (joined.Length <= limit) return joined;
        const string marker = "… Details truncated.";
        return joined[..(limit - marker.Length)] + marker;
    }

    static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        MaxDepth = 32,
    };

    static byte[] ReadBounded(string path, int maximumBytes)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete, 16 * 1024, FileOptions.SequentialScan);
        using var buffer = new MemoryStream(Math.Min((int)Math.Min(stream.Length, maximumBytes), 16 * 1024));
        byte[] chunk = new byte[16 * 1024];
        while (true)
        {
            int remaining = maximumBytes + 1 - checked((int)buffer.Length);
            if (remaining <= 0) break;
            int read = stream.Read(chunk, 0, Math.Min(chunk.Length, remaining));
            if (read == 0) break;
            buffer.Write(chunk, 0, read);
        }

        if (buffer.Length > maximumBytes)
            throw new InvalidDataException($"State file exceeds the {maximumBytes}-byte read limit.");
        return buffer.ToArray();
    }

    static void Validate(ShadowReviewSnapshot snapshot, string expectedDataDirectory, JsonElement? root)
    {
        if (snapshot.SchemaVersion != SchemaVersion)
            throw new InvalidDataException($"Unsupported shadow review schema version {snapshot.SchemaVersion}.");
        if (string.IsNullOrWhiteSpace(snapshot.DataDirectory))
            throw new InvalidDataException("State file has no data directory identity.");
        string actual = ControlEndpoint.NormalizeDataDirectory(snapshot.DataDirectory);
        StringComparison comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        if (!string.Equals(actual, expectedDataDirectory, comparison))
            throw new InvalidDataException("State file belongs to a different telemetry directory.");

        if (snapshot.Status is not ("WAITING" or "RUNNING" or "COMPLETED" or "FAILED" or "STOPPED"))
            throw new InvalidDataException("State file has an unknown scheduler status.");
        if (snapshot.UpdatedUtc == default || snapshot.Detail is null || snapshot.History is null ||
            snapshot.History.Length > 12 || snapshot.DataDirectory.Length > 4096 ||
            snapshot.Detail.Length > 4096 || snapshot.Status.Length > 32 ||
            snapshot.ActiveRunId?.Length > 128 ||
            snapshot.LastAttemptRunId is not null &&
            (string.IsNullOrWhiteSpace(snapshot.LastAttemptRunId) || snapshot.LastAttemptRunId.Length > 128) ||
            snapshot.RetainedReports is null || snapshot.RetainedReports.Length > MaximumRetainedReports ||
            snapshot.Recovery is null || snapshot.Recovery.Status is not ("NOT_CHECKED" or "OK" or "ATTENTION") ||
            snapshot.Recovery.Detail is null || snapshot.Recovery.Detail.Length > 2048 ||
            snapshot.Recovery.RecoveredCount < 0 ||
            snapshot.Recovery.CheckedUtc is DateTimeOffset checkedUtc && checkedUtc == default)
            throw new InvalidDataException("State file is missing required snapshot fields or exceeds history limits.");

        foreach (var report in snapshot.RetainedReports)
            if (report is null || !IsSafeRetainedReportBasename(report.ReportFileName) ||
                report.Detail is null || report.Detail.Length > 512)
                throw new InvalidDataException("State file contains an invalid retained report entry.");

        if (root is JsonElement rootElement)
        {
            var historyElement = rootElement.GetProperty("History");
            if (historyElement.ValueKind != JsonValueKind.Array || historyElement.GetArrayLength() > 12)
                throw new InvalidDataException("State history must be an array of at most 12 entries.");
            string[] summaryFields =
            [
                "RunId", "CompletedUtc", "InputCutoffUtc", "State", "Conclusion", "WinnerModel",
                "CohortKey", "ModelIdentity", "ConfigurationIdentity", "FrozenCutoffFingerprint",
                "SupportedDays", "TestDays", "FairHeldoutRows", "TestRows", "TrainingDays",
                "CalibrationDays", "AdvisoryTransitions", "AdvisoryTransitionsPerObservedHour",
                "PowerMinimumW", "PowerMaximumW", "CurrentMinimumA", "CurrentMaximumA",
                "GpuTemperatureMinimumC", "GpuTemperatureMaximumC", "TemperatureValidMinutes",
                "SourceRows", "RejectedRows", "InvalidRows", "Models", "SyntheticFaults", "Warnings",
                "ReportFileName",
            ];
            string[] modelFields =
            ["Name", "IsAvailable", "EligibleForRanking", "FairRows", "OutOfEnvelopeRows", "FairMaeV", "FairRmseV", "Detail"];
            string[] syntheticFields =
            ["Label", "ScenarioIdentity", "State", "OnsetUtc", "WindowEndUtc", "DetectionLatencySeconds", "AttributableTransitions", "Detail"];
            foreach (var item in historyElement.EnumerateArray())
            {
                RequireObject(item, "history entry");
                RequireProperties(item, summaryFields);
                RequireNestedArrays(item, "TrainingDays", "CalibrationDays", "Models", "SyntheticFaults", "Warnings");
                foreach (var model in item.GetProperty("Models").EnumerateArray())
                {
                    RequireObject(model, "model summary");
                    RequireProperties(model, modelFields);
                }
                foreach (var synthetic in item.GetProperty("SyntheticFaults").EnumerateArray())
                {
                    RequireObject(synthetic, "synthetic summary");
                    RequireProperties(synthetic, syntheticFields);
                }
            }

            ValidateOptionalRecoveryProperties(rootElement);
        }

        foreach (var item in snapshot.History)
            ValidateSummary(item);
    }

    internal static void ValidateSummary(ShadowReviewSummary item)
    {
        if (item is null || string.IsNullOrWhiteSpace(item.RunId) || item.RunId.Length > 128 || item.CompletedUtc == default ||
            item.InputCutoffUtc == default || item.State is null || item.Conclusion is null ||
            string.IsNullOrWhiteSpace(item.State) || item.State.Length > 64 || item.Conclusion.Length > 8192 ||
            string.IsNullOrWhiteSpace(item.CohortKey) || item.CohortKey.Length > 4096 ||
            string.IsNullOrWhiteSpace(item.ModelIdentity) || item.ModelIdentity.Length > 4096 ||
            string.IsNullOrWhiteSpace(item.ConfigurationIdentity) || item.ConfigurationIdentity.Length > 4096 ||
            string.IsNullOrWhiteSpace(item.FrozenCutoffFingerprint) || item.FrozenCutoffFingerprint.Length > 4096 ||
            item.SupportedDays < 0 || item.TestDays < 0 || item.FairHeldoutRows < 0 || item.TestRows < 0 ||
            item.AdvisoryTransitions < 0 || item.TemperatureValidMinutes < 0 || item.SourceRows < 0 ||
            item.RejectedRows < 0 || item.InvalidRows < 0 || item.TrainingDays is null ||
            item.CalibrationDays is null || item.Models is null || item.SyntheticFaults is null ||
            item.Warnings is null || item.TrainingDays.Length > 3660 || item.CalibrationDays.Length > 3660 ||
            item.Models.Length > 64 || item.SyntheticFaults.Length > 64 || item.Warnings.Length > 128 ||
            item.TrainingDays.Any(x => x is null || x.Length > 32) ||
            item.CalibrationDays.Any(x => x is null || x.Length > 32) ||
            item.Warnings.Any(x => x is null || x.Length > 4096) ||
            !IsSafeReportBasename(item.ReportFileName))
            throw new InvalidDataException("State file contains an incomplete history entry.");

        foreach (double? number in new[] { item.AdvisoryTransitionsPerObservedHour,
                     item.PowerMinimumW, item.PowerMaximumW, item.CurrentMinimumA, item.CurrentMaximumA,
                     item.GpuTemperatureMinimumC, item.GpuTemperatureMaximumC })
            if (number is double value && !double.IsFinite(value))
                throw new InvalidDataException("State file contains a non-finite metric.");

        foreach (var model in item.Models)
        {
            if (model is null || string.IsNullOrWhiteSpace(model.Name) || model.Name.Length > 4096 ||
                model.Detail is null || model.Detail.Length > 4096 ||
                model.FairRows < 0 || model.OutOfEnvelopeRows < 0 ||
                (model.FairMaeV is double mae && !double.IsFinite(mae)) ||
                (model.FairRmseV is double rmse && !double.IsFinite(rmse)))
                throw new InvalidDataException("State file contains an incomplete model summary.");
        }

        foreach (var synthetic in item.SyntheticFaults)
        {
            if (synthetic is null || string.IsNullOrWhiteSpace(synthetic.Label) || synthetic.Label.Length > 4096 ||
                string.IsNullOrWhiteSpace(synthetic.ScenarioIdentity) || synthetic.ScenarioIdentity.Length > 4096 ||
                string.IsNullOrWhiteSpace(synthetic.State) || synthetic.State.Length > 64 ||
                synthetic.Detail is null || synthetic.Detail.Length > 4096 || synthetic.AttributableTransitions < 0 ||
                (synthetic.DetectionLatencySeconds is double latency && !double.IsFinite(latency)))
                throw new InvalidDataException("State file contains an incomplete synthetic summary.");
        }
    }

    static void ValidateOptionalRecoveryProperties(JsonElement root)
    {
        if (root.TryGetProperty("LastAttemptRunId", out var runId) &&
            runId.ValueKind is not (JsonValueKind.Null or JsonValueKind.String))
            throw new InvalidDataException("State field LastAttemptRunId must be a string or null.");

        if (root.TryGetProperty("RetainedReports", out var retained))
        {
            if (retained.ValueKind != JsonValueKind.Array || retained.GetArrayLength() > MaximumRetainedReports)
                throw new InvalidDataException("State RetainedReports must be an array of at most 12 entries.");
            foreach (var report in retained.EnumerateArray())
            {
                RequireObject(report, "retained report");
                RequireProperties(report, "ReportFileName", "Detail");
            }
        }

        if (!root.TryGetProperty("Recovery", out var recovery)) return;
        RequireObject(recovery, "recovery information");
        if (recovery.TryGetProperty("Status", out var status) && status.ValueKind != JsonValueKind.String ||
            recovery.TryGetProperty("CheckedUtc", out var checkedUtc) &&
            checkedUtc.ValueKind is not (JsonValueKind.Null or JsonValueKind.String) ||
            recovery.TryGetProperty("RecoveredCount", out var count) && !count.TryGetInt32(out _) ||
            recovery.TryGetProperty("ScanIncomplete", out var incomplete) &&
            incomplete.ValueKind is not (JsonValueKind.True or JsonValueKind.False) ||
            recovery.TryGetProperty("Detail", out var detail) && detail.ValueKind != JsonValueKind.String)
            throw new InvalidDataException("State Recovery information contains a malformed field.");
    }

    internal static void ValidateSummaryElement(JsonElement item)
    {
        RequireObject(item, "history entry");
        string[] summaryFields =
        [
            "RunId", "CompletedUtc", "InputCutoffUtc", "State", "Conclusion", "WinnerModel",
            "CohortKey", "ModelIdentity", "ConfigurationIdentity", "FrozenCutoffFingerprint",
            "SupportedDays", "TestDays", "FairHeldoutRows", "TestRows", "TrainingDays",
            "CalibrationDays", "AdvisoryTransitions", "AdvisoryTransitionsPerObservedHour",
            "PowerMinimumW", "PowerMaximumW", "CurrentMinimumA", "CurrentMaximumA",
            "GpuTemperatureMinimumC", "GpuTemperatureMaximumC", "TemperatureValidMinutes",
            "SourceRows", "RejectedRows", "InvalidRows", "Models", "SyntheticFaults", "Warnings",
            "ReportFileName",
        ];
        string[] modelFields =
        ["Name", "IsAvailable", "EligibleForRanking", "FairRows", "OutOfEnvelopeRows", "FairMaeV", "FairRmseV", "Detail"];
        string[] syntheticFields =
        ["Label", "ScenarioIdentity", "State", "OnsetUtc", "WindowEndUtc", "DetectionLatencySeconds", "AttributableTransitions", "Detail"];
        RequireProperties(item, summaryFields);
        RequireNestedArrays(item, "TrainingDays", "CalibrationDays", "Models", "SyntheticFaults", "Warnings");
        foreach (var model in item.GetProperty("Models").EnumerateArray())
        {
            RequireObject(model, "model summary");
            RequireProperties(model, modelFields);
        }
        foreach (var synthetic in item.GetProperty("SyntheticFaults").EnumerateArray())
        {
            RequireObject(synthetic, "synthetic summary");
            RequireProperties(synthetic, syntheticFields);
        }
    }

    static bool IsSafeReportBasename(string value) =>
        !string.IsNullOrWhiteSpace(value) &&
        value.EndsWith(".json", StringComparison.OrdinalIgnoreCase) &&
        string.Equals(Path.GetFileName(value), value, StringComparison.Ordinal) &&
        value is not ("." or "..") &&
        !value.Contains('/') && !value.Contains('\\') && !value.Contains(':') &&
        !value.Any(char.IsControl) &&
        (OperatingSystem.IsWindows() ? value.IndexOfAny(Path.GetInvalidFileNameChars()) < 0 : true);

    internal static bool IsSafeRetainedReportBasename(string? value) =>
        !string.IsNullOrWhiteSpace(value) && IsSafeReportBasename(value);

    static void RequireNestedArrays(JsonElement value, params string[] names)
    {
        foreach (string name in names)
            if (!value.TryGetProperty(name, out var property) || property.ValueKind != JsonValueKind.Array)
                throw new InvalidDataException($"State field {name} must be an array.");
        if (value.GetProperty("TrainingDays").GetArrayLength() > 3660 ||
            value.GetProperty("CalibrationDays").GetArrayLength() > 3660 ||
            value.GetProperty("Models").GetArrayLength() > 64 ||
            value.GetProperty("SyntheticFaults").GetArrayLength() > 64 ||
            value.GetProperty("Warnings").GetArrayLength() > 128)
            throw new InvalidDataException("State history arrays exceed their read limits.");
    }

    static void RequireObject(JsonElement value, string name)
    {
        if (value.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException($"State {name} must be a JSON object.");
    }

    static void RequireProperties(JsonElement value, params string[] names)
    {
        foreach (string name in names)
            if (!value.TryGetProperty(name, out _))
                throw new InvalidDataException($"State file is missing required field {name}.");
    }

    sealed class BoundedWriteStream : Stream
    {
        readonly Stream inner;
        readonly int maximumBytes;
        int written;

        public BoundedWriteStream(Stream inner, int maximumBytes)
        {
            this.inner = inner;
            this.maximumBytes = maximumBytes;
        }

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => written;
        public override long Position { get => written; set => throw new NotSupportedException(); }
        public override void Flush() => inner.Flush();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count)
        {
            CheckCount(count);
            inner.Write(buffer, offset, count);
            written += count;
        }

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            CheckCount(buffer.Length);
            inner.Write(buffer);
            written += buffer.Length;
        }

        void CheckCount(int count)
        {
            if (count < 0 || (long)written + count > maximumBytes)
                throw new InvalidDataException($"Shadow review state exceeds the {maximumBytes}-byte write limit.");
        }
    }
}

public sealed record ShadowReviewSnapshot
{
    public int SchemaVersion { get; init; } = ShadowReviewStore.SchemaVersion;
    public string DataDirectory { get; init; } = string.Empty;
    public string Status { get; init; } = "WAITING";
    public string Detail { get; init; } = string.Empty;
    public DateTimeOffset UpdatedUtc { get; init; }
    public DateTimeOffset? StartedUtc { get; init; }
    public DateTimeOffset? NextAttemptUtc { get; init; }
    public DateTimeOffset? LastSuccessUtc { get; init; }
    public string? ActiveRunId { get; init; }
    public string? LastAttemptRunId { get; init; }
    public ShadowReviewSummary[] History { get; init; } = [];
    public ShadowReviewRetainedReport[] RetainedReports { get; init; } = [];
    public ShadowReviewRecoveryInfo Recovery { get; init; } = new();
}

public sealed record ShadowReviewRetainedReport
{
    public string ReportFileName { get; init; } = string.Empty;
    public string Detail { get; init; } = string.Empty;
}

public sealed record ShadowReviewRecoveryInfo
{
    public string Status { get; init; } = "NOT_CHECKED";
    public DateTimeOffset? CheckedUtc { get; init; }
    public int RecoveredCount { get; init; }
    public bool ScanIncomplete { get; init; }
    public string Detail { get; init; } = string.Empty;
}

public sealed record ShadowReviewSummary
{
    public string RunId { get; init; } = string.Empty;
    public DateTimeOffset CompletedUtc { get; init; }
    public DateTimeOffset InputCutoffUtc { get; init; }
    public string State { get; init; } = string.Empty;
    public string Conclusion { get; init; } = string.Empty;
    public string? WinnerModel { get; init; }
    public string CohortKey { get; init; } = string.Empty;
    public string ModelIdentity { get; init; } = string.Empty;
    public string ConfigurationIdentity { get; init; } = string.Empty;
    public string FrozenCutoffFingerprint { get; init; } = string.Empty;
    public int SupportedDays { get; init; }
    public int TestDays { get; init; }
    public int FairHeldoutRows { get; init; }
    public int TestRows { get; init; }
    public string[] TrainingDays { get; init; } = [];
    public string[] CalibrationDays { get; init; } = [];
    public int AdvisoryTransitions { get; init; }
    public double? AdvisoryTransitionsPerObservedHour { get; init; }
    public double? PowerMinimumW { get; init; }
    public double? PowerMaximumW { get; init; }
    public double? CurrentMinimumA { get; init; }
    public double? CurrentMaximumA { get; init; }
    public double? GpuTemperatureMinimumC { get; init; }
    public double? GpuTemperatureMaximumC { get; init; }
    public int TemperatureValidMinutes { get; init; }
    public long SourceRows { get; init; }
    public long RejectedRows { get; init; }
    public long InvalidRows { get; init; }
    public ShadowReviewModelSummary[] Models { get; init; } = [];
    public ShadowReviewSyntheticSummary[] SyntheticFaults { get; init; } = [];
    public string[] Warnings { get; init; } = [];
    public string ReportFileName { get; init; } = string.Empty;
}

public sealed record ShadowReviewModelSummary
{
    public string Name { get; init; } = string.Empty;
    public bool IsAvailable { get; init; }
    public bool EligibleForRanking { get; init; }
    public int FairRows { get; init; }
    public int OutOfEnvelopeRows { get; init; }
    public double? FairMaeV { get; init; }
    public double? FairRmseV { get; init; }
    public string Detail { get; init; } = string.Empty;
}

public sealed record ShadowReviewSyntheticSummary
{
    public string Label { get; init; } = string.Empty;
    public string ScenarioIdentity { get; init; } = string.Empty;
    public string State { get; init; } = "UNAVAILABLE";
    public DateTimeOffset? OnsetUtc { get; init; }
    public DateTimeOffset? WindowEndUtc { get; init; }
    public double? DetectionLatencySeconds { get; init; }
    public int AttributableTransitions { get; init; }
    public string Detail { get; init; } = string.Empty;
}
