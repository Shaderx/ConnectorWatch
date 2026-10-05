using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security;
using System.Security.Cryptography;
using System.Text.Json;

namespace ConnectorWatch;

internal sealed record ShadowReviewReconcileResult(
    ShadowReviewSnapshot Snapshot,
    bool Changed,
    bool ProjectionWasReplaced);

/// <summary>Publishes durable completion receipts and reconciles the disk-only review projection.</summary>
internal sealed class ShadowReviewRecoveryStore
{
    const int MaximumDirectoryEntries = 4096;
    const int MaximumReceiptCandidates = 128;
    const int MaximumReceiptBytes = 512 * 1024;
    const int MaximumReportBytes = 16 * 1024 * 1024;
    const long MaximumScanBytes = 256L * 1024 * 1024;
    const string RetainedDetail = "This retained report is not in the current accepted history. Scheduled completion is unverified.";
    static readonly string FileStampFormat = "yyyyMMdd'T'HHmmssfff'Z'";

    readonly string dataDirectory;
    readonly string storeDirectory;
    readonly string statePath;
    readonly string reportsDirectory;
    readonly string completionsDirectory;
    readonly Dictionary<string, CachedReceipt> cache = new(StringComparer.OrdinalIgnoreCase);

    static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = false,
        WriteIndented = true,
        MaxDepth = 32,
    };

    internal ShadowReviewRecoveryStore(string normalizedDataDirectory, string storeDirectory)
    {
        dataDirectory = ControlEndpoint.NormalizeDataDirectory(normalizedDataDirectory);
        this.storeDirectory = Path.GetFullPath(storeDirectory);
        statePath = Path.Combine(this.storeDirectory, "state.json");
        reportsDirectory = Path.Combine(this.storeDirectory, "reports");
        completionsDirectory = Path.Combine(this.storeDirectory, "completions");
    }

    internal void PublishCompletion(
        ShadowReviewSummary summary,
        DateTimeOffset startedUtc,
        byte[] acceptedReportBytes)
    {
        if (summary is null) throw new ArgumentNullException(nameof(summary));
        if (acceptedReportBytes is null) throw new ArgumentNullException(nameof(acceptedReportBytes));
        if (acceptedReportBytes.Length == 0 || acceptedReportBytes.Length > MaximumReportBytes)
            throw new InvalidDataException("Accepted report bytes are outside the report size limit.");

        summary = ShadowReviewStore.PrepareCompletionSummary(summary, dataDirectory);
        ValidateAcceptedCompletion(summary, startedUtc);

        string reportStem = Path.GetFileNameWithoutExtension(summary.ReportFileName);
        string receiptName = reportStem + ".completion.json";
        string receiptPath = DirectChildPath(completionsDirectory, receiptName);
        string expectedReportPath = DirectChildPath(reportsDirectory, summary.ReportFileName);
        EnsurePlainDirectory(storeDirectory);
        EnsurePlainDirectory(reportsDirectory);
        if (!IsRegularFile(expectedReportPath))
            throw new InvalidDataException("The accepted report is missing or is not a regular file.");
        byte[] onDiskReport = ReadSimpleBounded(expectedReportPath, MaximumReportBytes);
        if (!onDiskReport.AsSpan().SequenceEqual(acceptedReportBytes))
            throw new InvalidDataException("The accepted report changed before its completion record was published.");

        string hash = Convert.ToHexString(SHA256.HashData(acceptedReportBytes));
        var receipt = new CompletionReceipt
        {
            SchemaVersion = 1,
            DataDirectory = dataDirectory,
            RunId = summary.RunId,
            StartedUtc = startedUtc.ToUniversalTime(),
            CompletedUtc = summary.CompletedUtc.ToUniversalTime(),
            InputCutoffUtc = summary.InputCutoffUtc.ToUniversalTime(),
            ReportFileName = summary.ReportFileName,
            ReportLength = acceptedReportBytes.LongLength,
            ReportSha256 = hash,
            Summary = summary,
        };
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(receipt, JsonOptions);
        if (bytes.Length > MaximumReceiptBytes)
            throw new InvalidDataException($"Completion record exceeds the {MaximumReceiptBytes}-byte write limit.");

        Directory.CreateDirectory(completionsDirectory);
        EnsurePlainDirectory(completionsDirectory);
        string temporary = Path.Combine(completionsDirectory, ".completion-" + Guid.NewGuid().ToString("N") + ".tmp");
        bool published = false;
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                       16 * 1024, FileOptions.WriteThrough))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }

            try
            {
                File.Move(temporary, receiptPath, overwrite: false);
                published = true;
            }
            catch (IOException) when (File.Exists(receiptPath))
            {
                if (!IsIdenticalReceipt(receiptPath, bytes, receipt))
                    throw new InvalidDataException("A conflicting completion record already exists for this run.");
                published = true;
            }
        }
        finally
        {
            if (File.Exists(temporary))
            {
                try { File.Delete(temporary); }
                catch when (published) { }
            }
        }

        if (!published)
            throw new IOException("Completion record was not committed.");
        cache.Remove(receiptPath);
    }

    internal ShadowReviewReconcileResult Reconcile(ShadowReviewSnapshot snapshot, DateTimeOffset now)
    {
        if (snapshot is null) throw new ArgumentNullException(nameof(snapshot));
        now = now.ToUniversalTime();
        var diskSnapshot = ShadowReviewStore.ReadAtPath(dataDirectory, statePath, out string? diskError);
        bool projectionWasReplaced = diskSnapshot is null || !SnapshotsEqual(diskSnapshot, snapshot);
        if (projectionWasReplaced) cache.Clear();

        bool scanIncomplete = false;
        bool attention = !string.IsNullOrWhiteSpace(diskError);
        var diagnostics = new List<string>();
        if (!string.IsNullOrWhiteSpace(diskError))
            AddDiagnostic(diagnostics, "The saved review state could not be read; recovery is using the scheduler's in-memory snapshot.");

        var completionEntries = ReadDirectoryEntries(completionsDirectory, "completion records",
            ref scanIncomplete, diagnostics);
        var reportEntries = ReadDirectoryEntries(reportsDirectory, "reports",
            ref scanIncomplete, diagnostics);

        var receiptCandidates = completionEntries
            .Where(item => item.IsFile && item.Name.EndsWith(".completion.json", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(item => item.Name, StringComparer.Ordinal)
            .ToArray();
        foreach (var malformed in receiptCandidates.Where(item => !IsCanonicalReceiptFileName(item.Name)))
        {
            attention = true;
            AddDiagnostic(diagnostics, $"Ignored a completion record with a non-canonical name: {malformed.Name}");
        }
        receiptCandidates = receiptCandidates.Where(item => IsCanonicalReceiptFileName(item.Name)).ToArray();
        if (receiptCandidates.Length > MaximumReceiptCandidates)
        {
            scanIncomplete = true;
            AddDiagnostic(diagnostics, $"The completion scan found more than {MaximumReceiptCandidates} candidates; only the newest canonical names were checked.");
            receiptCandidates = receiptCandidates.Take(MaximumReceiptCandidates).ToArray();
        }

        long bytesRead = 0;
        var validReceipts = new List<ValidatedReceipt>();
        var seenReceiptPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in receiptCandidates)
        {
            string receiptPath;
            try { receiptPath = DirectChildPath(completionsDirectory, entry.Name); }
            catch (Exception ex) when (IsStorageException(ex))
            {
                attention = true;
                AddDiagnostic(diagnostics, $"Ignored a completion record with an invalid path: {entry.Name}");
                continue;
            }
            seenReceiptPaths.Add(receiptPath);
            try
            {
                var result = ReadAndValidateReceipt(entry, receiptPath, ref bytesRead);
                if (result is not null) validReceipts.Add(result);
            }
            catch (Exception ex) when (IsStorageException(ex))
            {
                attention = true;
                if (ex is ScanIncompleteException)
                {
                    scanIncomplete = true;
                    AddDiagnostic(diagnostics, $"Could not fully validate completion record {entry.Name}: {ex.Message}");
                }
                else
                {
                    AddDiagnostic(diagnostics, $"Ignored invalid completion record {entry.Name}: {ex.Message}");
                }
                cache.Remove(receiptPath);
            }
        }

        foreach (string stale in cache.Keys.Where(key => !seenReceiptPaths.Contains(key)).ToArray())
            cache.Remove(stale);

        var grouped = validReceipts.GroupBy(item => item.Receipt.RunId, StringComparer.Ordinal).ToArray();
        var conflictingRunIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var group in grouped)
        {
            var first = group.First();
            if (group.Skip(1).Any(other => !SameSummary(first.Receipt.Summary, other.Receipt.Summary) ||
                                          !string.Equals(first.Receipt.ReportFileName, other.Receipt.ReportFileName, StringComparison.Ordinal)))
            {
                conflictingRunIds.Add(group.Key);
                attention = true;
                AddDiagnostic(diagnostics, $"Conflicting completion records refer to run {group.Key}; the existing accepted history is preserved.");
            }
        }

        var receiptsByRun = validReceipts
            .Where(item => !conflictingRunIds.Contains(item.Receipt.RunId))
            .GroupBy(item => item.Receipt.RunId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);

        var currentByRun = new Dictionary<string, ShadowReviewSummary>(StringComparer.Ordinal);
        foreach (var accepted in snapshot.History)
        {
            if (!currentByRun.TryAdd(accepted.RunId, accepted) &&
                !SameSummary(currentByRun[accepted.RunId], accepted))
            {
                attention = true;
                conflictingRunIds.Add(accepted.RunId);
                AddDiagnostic(diagnostics, $"The saved history contains conflicting entries for run {accepted.RunId}; the first accepted entry is preserved.");
            }
        }

        foreach (var pair in receiptsByRun)
        {
            if (!currentByRun.TryGetValue(pair.Key, out var saved)) continue;
            if (SameSummary(saved, pair.Value.Receipt.Summary)) continue;
            attention = true;
            conflictingRunIds.Add(pair.Key);
            AddDiagnostic(diagnostics, $"Completion record for run {pair.Key} conflicts with accepted history; the accepted summary is preserved.");
        }

        var summaries = new List<ShadowReviewSummary>(currentByRun.Values);
        foreach (var pair in receiptsByRun)
            if (!currentByRun.ContainsKey(pair.Key) && !conflictingRunIds.Contains(pair.Key))
                summaries.Add(pair.Value.Receipt.Summary);

        var mergedHistory = summaries
            .OrderByDescending(item => item.CompletedUtc)
            .ThenByDescending(item => item.RunId, StringComparer.Ordinal)
            .Take(ShadowReviewStore.MaximumHistoryEntries)
            .ToArray();
        var mergedRunIds = new HashSet<string>(mergedHistory.Select(item => item.RunId), StringComparer.Ordinal);
        int recoveredCount = receiptsByRun.Count(pair => !currentByRun.ContainsKey(pair.Key) &&
            !conflictingRunIds.Contains(pair.Key) && mergedRunIds.Contains(pair.Key));

        var trustedReportNames = new HashSet<string>(
            snapshot.History.Select(item => item.ReportFileName)
                .Concat(receiptsByRun.Where(pair => !conflictingRunIds.Contains(pair.Key))
                    .Select(pair => pair.Value.Receipt.ReportFileName)),
            OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        var retainedCandidates = reportEntries
            .Where(item => item.IsFile && item.Name.EndsWith(".json", StringComparison.OrdinalIgnoreCase) &&
                           ShadowReviewStore.IsSafeRetainedReportBasename(item.Name) &&
                           !trustedReportNames.Contains(item.Name))
            .OrderByDescending(item => item.Name, StringComparer.Ordinal)
            .ToArray();
        if (retainedCandidates.Length > ShadowReviewStore.MaximumRetainedReports)
        {
            attention = true;
            AddDiagnostic(diagnostics, $"Only {ShadowReviewStore.MaximumRetainedReports} retained report names are shown; additional files remain unverified.");
        }
        var retainedReports = retainedCandidates.Take(ShadowReviewStore.MaximumRetainedReports)
            .Select(item => new ShadowReviewRetainedReport { ReportFileName = item.Name, Detail = RetainedDetail })
            .ToArray();

        ShadowReviewSnapshot reconciled = snapshot with { History = mergedHistory };
        reconciled = ApplyRecoveredSchedule(reconciled, snapshot, receiptsByRun, conflictingRunIds,
            scanIncomplete, diagnostics, ref attention);

        if (scanIncomplete) attention = true;
        string recoveryDetail = string.Join(" ", diagnostics);
        if (recoveryDetail.Length > 2048)
            recoveryDetail = recoveryDetail[..(2048 - "… Recovery details truncated.".Length)] + "… Recovery details truncated.";
        var recovery = new ShadowReviewRecoveryInfo
        {
            Status = attention || scanIncomplete ? "ATTENTION" : "OK",
            CheckedUtc = now,
            RecoveredCount = recoveredCount,
            ScanIncomplete = scanIncomplete,
            Detail = recoveryDetail,
        };
        reconciled = reconciled with { RetainedReports = retainedReports, Recovery = recovery };

        bool changed = projectionWasReplaced || !SnapshotsEqual(snapshot, reconciled);
        return new ShadowReviewReconcileResult(reconciled, changed, projectionWasReplaced);
    }

    ShadowReviewSnapshot ApplyRecoveredSchedule(
        ShadowReviewSnapshot mergedSnapshot,
        ShadowReviewSnapshot priorSnapshot,
        IReadOnlyDictionary<string, ValidatedReceipt> receiptsByRun,
        HashSet<string> conflictingRunIds,
        bool scanIncomplete,
        List<string> diagnostics,
        ref bool attention)
    {
        var receiptCandidates = receiptsByRun.Values
            .Where(item => !conflictingRunIds.Contains(item.Receipt.RunId))
            .OrderByDescending(item => item.Receipt.CompletedUtc)
            .ThenByDescending(item => item.Receipt.RunId, StringComparer.Ordinal)
            .ToArray();
        if (receiptCandidates.Length == 0) return mergedSnapshot;

        var matchingAttempt = receiptCandidates.FirstOrDefault(item =>
            string.Equals(item.Receipt.RunId, priorSnapshot.ActiveRunId, StringComparison.Ordinal) ||
            string.Equals(item.Receipt.RunId, priorSnapshot.LastAttemptRunId, StringComparison.Ordinal));
        DateTimeOffset? lastSuccess = priorSnapshot.LastSuccessUtc ?? priorSnapshot.History.FirstOrDefault()?.CompletedUtc;

        if (matchingAttempt is not null)
        {
            if (!scanIncomplete)
            {
                var newerDistinct = receiptCandidates.FirstOrDefault(item =>
                    !string.Equals(item.Receipt.RunId, matchingAttempt.Receipt.RunId, StringComparison.Ordinal) &&
                    item.Receipt.CompletedUtc > matchingAttempt.Receipt.CompletedUtc &&
                    item.Receipt.StartedUtc > matchingAttempt.Receipt.StartedUtc &&
                    (!lastSuccess.HasValue || item.Receipt.CompletedUtc > lastSuccess.Value) &&
                    receiptCandidates.All(other => other.Receipt.RunId == item.Receipt.RunId ||
                        item.Receipt.StartedUtc > other.Receipt.StartedUtc &&
                        item.Receipt.CompletedUtc > other.Receipt.CompletedUtc));
                if (newerDistinct is not null) return CompleteRecoveredAttempt(mergedSnapshot, newerDistinct.Receipt);

                if (receiptCandidates.Any(item =>
                        !string.Equals(item.Receipt.RunId, matchingAttempt.Receipt.RunId, StringComparison.Ordinal) &&
                        (item.Receipt.CompletedUtc > matchingAttempt.Receipt.CompletedUtc &&
                         item.Receipt.StartedUtc <= matchingAttempt.Receipt.StartedUtc ||
                         item.Receipt.StartedUtc >= matchingAttempt.Receipt.StartedUtc &&
                         item.Receipt.CompletedUtc <= matchingAttempt.Receipt.CompletedUtc)))
                {
                    attention = true;
                    AddDiagnostic(diagnostics, "A different recovered attempt has ambiguous clock ordering; the matching run was restored.");
                }
            }
            else if (receiptCandidates.Any(item =>
                         !string.Equals(item.Receipt.RunId, matchingAttempt.Receipt.RunId, StringComparison.Ordinal)))
            {
                AddDiagnostic(diagnostics, "A partial receipt scan did not replace the matching recovered attempt.");
            }
            return CompleteRecoveredAttempt(mergedSnapshot, matchingAttempt.Receipt);
        }

        if (priorSnapshot.Status == "WAITING" && priorSnapshot.History.Length == 0 &&
            priorSnapshot.ActiveRunId is null && priorSnapshot.LastAttemptRunId is null)
            return CompleteRecoveredAttempt(mergedSnapshot, receiptCandidates[0].Receipt);

        var advancing = receiptCandidates.FirstOrDefault(item =>
            !lastSuccess.HasValue || item.Receipt.CompletedUtc > lastSuccess.Value);
        if (advancing is null) return mergedSnapshot;
        if (scanIncomplete)
        {
            AddDiagnostic(diagnostics, "A partial receipt scan did not change the current attempt schedule.");
            return mergedSnapshot;
        }

        DateTimeOffset attemptStarted = priorSnapshot.StartedUtc ?? priorSnapshot.UpdatedUtc;
        if (attemptStarted == default || advancing.Receipt.StartedUtc <= attemptStarted ||
            receiptCandidates.Any(other => other.Receipt.RunId != advancing.Receipt.RunId &&
                (other.Receipt.StartedUtc >= advancing.Receipt.StartedUtc ||
                 other.Receipt.CompletedUtc >= advancing.Receipt.CompletedUtc)))
        {
            attention = true;
            AddDiagnostic(diagnostics, "Recovered completion ordering is ambiguous; the saved attempt schedule was preserved.");
            return mergedSnapshot;
        }

        return CompleteRecoveredAttempt(mergedSnapshot, advancing.Receipt);
    }

    static ShadowReviewSnapshot CompleteRecoveredAttempt(ShadowReviewSnapshot snapshot, CompletionReceipt receipt)
    {
        return snapshot with
        {
            Status = "COMPLETED",
            Detail = receipt.Summary.State == "INSUFFICIENT_DATA"
                ? "Review completed. The selected cohort does not yet meet the evaluator's coverage requirements."
                : "Review completed successfully.",
            UpdatedUtc = receipt.CompletedUtc,
            StartedUtc = receipt.StartedUtc,
            NextAttemptUtc = receipt.CompletedUtc + ShadowReviewScheduler.ReviewPeriod,
            LastSuccessUtc = receipt.CompletedUtc,
            ActiveRunId = null,
            LastAttemptRunId = receipt.RunId,
        };
    }

    ValidatedReceipt? ReadAndValidateReceipt(
        DirectoryEntry entry,
        string receiptPath,
        ref long bytesRead)
    {
        EnsurePlainDirectory(storeDirectory);
        EnsurePlainDirectory(completionsDirectory);
        EnsurePlainDirectory(reportsDirectory);
        if (!IsRegularFile(receiptPath))
            throw new InvalidDataException("Completion record is missing or is not a regular file.");

        var receiptInfo = new FileInfo(receiptPath);
        long receiptLength = receiptInfo.Length;
        DateTime receiptWriteUtc = receiptInfo.LastWriteTimeUtc;
        if (receiptLength > MaximumReceiptBytes)
            throw new ScanIncompleteException($"Completion record exceeds the {MaximumReceiptBytes}-byte read limit.");

        if (!TryGetReceiptReportName(entry.Name, out string reportName))
            throw new InvalidDataException("Completion filename does not identify a safe report.");
        string reportPath = DirectChildPath(reportsDirectory, reportName);
        if (!IsRegularFile(reportPath))
            throw new InvalidDataException("The associated JSON report is missing or is not a regular file.");
        var reportInfo = new FileInfo(reportPath);
        long reportLength = reportInfo.Length;
        DateTime reportWriteUtc = reportInfo.LastWriteTimeUtc;
        if (reportLength > MaximumReportBytes)
            throw new ScanIncompleteException($"Associated report exceeds the {MaximumReportBytes}-byte read limit.");

        if (cache.TryGetValue(receiptPath, out var cached) &&
            cached.ReceiptLength == receiptLength && cached.ReceiptWriteUtc == receiptWriteUtc &&
            cached.ReportLength == reportLength && cached.ReportWriteUtc == reportWriteUtc)
        {
            receiptInfo.Refresh();
            reportInfo.Refresh();
            if (!IsRegularFile(receiptPath) || !IsRegularFile(reportPath) ||
                receiptInfo.Length != receiptLength || receiptInfo.LastWriteTimeUtc != receiptWriteUtc ||
                reportInfo.Length != reportLength || reportInfo.LastWriteTimeUtc != reportWriteUtc)
                throw new ScanIncompleteException("Completion record or report changed during cached validation.");
            return new ValidatedReceipt(cached.Receipt, receiptPath, receiptLength, receiptWriteUtc, reportPath, reportLength, reportWriteUtc);
        }

        CompletionReceipt receipt;
        if (cache.TryGetValue(receiptPath, out cached) &&
            cached.ReceiptLength == receiptLength && cached.ReceiptWriteUtc == receiptWriteUtc)
        {
            receipt = cached.Receipt;
        }
        else
        {
            byte[] receiptBytes = ReadContentBounded(receiptPath, MaximumReceiptBytes,
                MaximumScanBytes - bytesRead, ref bytesRead, out bool exhausted);
            if (exhausted)
                throw new ScanIncompleteException("The scan content budget was reached before the completion record could be read.");
            receipt = ParseAndValidateReceipt(receiptBytes, entry.Name, reportName);
        }

        if (reportLength > MaximumScanBytes - bytesRead)
            throw new ScanIncompleteException("The scan content budget was reached before the associated report could be checked.");
        byte[] reportBytes = ReadContentBounded(reportPath, MaximumReportBytes,
            MaximumScanBytes - bytesRead, ref bytesRead, out bool reportExhausted);
        if (reportExhausted)
            throw new ScanIncompleteException("The scan content budget was reached before the associated report could be checked.");
        ValidateReportBinding(receipt, reportBytes);

        receiptInfo.Refresh();
        reportInfo.Refresh();
        if (receiptInfo.Length != receiptLength || receiptInfo.LastWriteTimeUtc != receiptWriteUtc ||
            reportInfo.Length != reportLength || reportInfo.LastWriteTimeUtc != reportWriteUtc)
            throw new InvalidDataException("Completion record or report changed during validation.");

        var validated = new ValidatedReceipt(receipt, receiptPath, receiptLength, receiptWriteUtc,
            reportPath, reportLength, reportWriteUtc);
        cache[receiptPath] = new CachedReceipt(receipt, receiptLength, receiptWriteUtc, reportLength, reportWriteUtc);
        return validated;
    }

    CompletionReceipt ParseAndValidateReceipt(byte[] bytes, string receiptFileName, string expectedReportFileName)
    {
        using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions
        {
            AllowTrailingCommas = false,
            CommentHandling = JsonCommentHandling.Disallow,
            MaxDepth = 32,
        });
        JsonElement root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("Completion record must be an object.");
        RequireProperties(root, "SchemaVersion", "DataDirectory", "RunId", "StartedUtc", "CompletedUtc",
            "InputCutoffUtc", "ReportFileName", "ReportLength", "ReportSha256", "Summary");
        JsonElement summaryElement = root.GetProperty("Summary");
        ShadowReviewStore.ValidateSummaryElement(summaryElement);
        var receipt = JsonSerializer.Deserialize<CompletionReceipt>(bytes, JsonOptions)
            ?? throw new InvalidDataException("Completion record did not contain a receipt.");
        if (receipt.SchemaVersion != 1)
            throw new InvalidDataException($"Unsupported completion record schema version {receipt.SchemaVersion}.");
        if (string.IsNullOrWhiteSpace(receipt.DataDirectory) ||
            !SameDirectoryIdentity(receipt.DataDirectory, dataDirectory))
            throw new InvalidDataException("Completion record belongs to a different telemetry directory.");
        if (!Guid.TryParseExact(receipt.RunId, "N", out var runId) ||
            !string.Equals(runId.ToString("N"), receipt.RunId, StringComparison.Ordinal))
            throw new InvalidDataException("Completion record has a non-canonical run identifier.");
        ValidateReceiptTimestamps(receipt);
        if (!ShadowReviewStore.IsSafeRetainedReportBasename(receipt.ReportFileName) ||
            !string.Equals(receipt.ReportFileName, expectedReportFileName, StringComparison.Ordinal))
            throw new InvalidDataException("Completion record has an unsafe or mismatched report filename.");
        string expectedReceiptName = Path.GetFileNameWithoutExtension(receipt.ReportFileName) + ".completion.json";
        if (!string.Equals(receiptFileName, expectedReceiptName, StringComparison.Ordinal))
            throw new InvalidDataException("Completion record filename does not match its report basename.");
        if (receipt.ReportLength <= 0 || receipt.ReportLength > MaximumReportBytes)
            throw new InvalidDataException("Completion record report length is outside the accepted report size limit.");
        if (!IsSha256(receipt.ReportSha256))
            throw new InvalidDataException("Completion record has an invalid SHA-256 value.");
        ShadowReviewStore.ValidateSummary(receipt.Summary);
        var compactSummary = ShadowReviewStore.PrepareCompletionSummary(receipt.Summary, dataDirectory);
        if (!SameSummary(compactSummary, receipt.Summary))
            throw new InvalidDataException("Completion summary is not in its bounded accepted form.");
        ValidateAcceptedCompletion(receipt.Summary, receipt.StartedUtc);
        if (!string.Equals(receipt.Summary.RunId, receipt.RunId, StringComparison.Ordinal) ||
            receipt.Summary.CompletedUtc.ToUniversalTime() != receipt.CompletedUtc.ToUniversalTime() ||
            receipt.Summary.InputCutoffUtc.ToUniversalTime() != receipt.InputCutoffUtc.ToUniversalTime() ||
            !string.Equals(receipt.Summary.ReportFileName, receipt.ReportFileName, StringComparison.Ordinal))
            throw new InvalidDataException("Completion record fields do not agree with its accepted summary.");
        return receipt;
    }

    static void ValidateAcceptedCompletion(ShadowReviewSummary summary, DateTimeOffset startedUtc)
    {
        if (startedUtc == default || summary.CompletedUtc == default || summary.InputCutoffUtc == default)
            throw new InvalidDataException("Completion record has a missing timestamp.");
        if (summary.State is not ("SUFFICIENT" or "INSUFFICIENT_DATA"))
            throw new InvalidDataException("Only accepted SUFFICIENT or INSUFFICIENT_DATA summaries can be committed.");
        if (!ShadowReviewStore.IsSafeRetainedReportBasename(summary.ReportFileName))
            throw new InvalidDataException("Completion report filename is unsafe.");
        if (!Guid.TryParseExact(summary.RunId, "N", out var runId) ||
            !string.Equals(runId.ToString("N"), summary.RunId, StringComparison.Ordinal))
            throw new InvalidDataException("Completion summary has a non-canonical run identifier.");

        DateTimeOffset startUtc = startedUtc.ToUniversalTime();
        DateTimeOffset expectedCutoff = new(startUtc.UtcDateTime.Date, TimeSpan.Zero);
        if (summary.InputCutoffUtc.ToUniversalTime() != expectedCutoff)
            throw new InvalidDataException("Completion summary cutoff does not match the launched review day.");

        string stamp = summary.ReportFileName[..^".json".Length];
        int separator = stamp.LastIndexOf('-');
        if (separator <= 0 || !string.Equals(stamp[(separator + 1)..], summary.RunId, StringComparison.Ordinal))
            throw new InvalidDataException("Completion report filename does not match its run identifier.");
        string timestamp = stamp[..separator];
        if (!DateTime.TryParseExact(timestamp, FileStampFormat, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out _))
            throw new InvalidDataException("Completion report filename does not use the scheduler's canonical format.");
        if (!string.Equals(timestamp,
                startedUtc.ToUniversalTime().UtcDateTime.ToString(FileStampFormat, CultureInfo.InvariantCulture),
                StringComparison.Ordinal))
            throw new InvalidDataException("Completion report filename does not match the attempt start time.");
    }

    static void ValidateReceiptTimestamps(CompletionReceipt receipt)
    {
        if (receipt.StartedUtc == default || receipt.CompletedUtc == default || receipt.InputCutoffUtc == default)
            throw new InvalidDataException("Completion record has a missing timestamp.");
        DateTimeOffset expectedCutoff = new(receipt.StartedUtc.UtcDateTime.Date, TimeSpan.Zero);
        if (receipt.InputCutoffUtc.ToUniversalTime() != expectedCutoff)
            throw new InvalidDataException("Completion record cutoff does not match its launch day.");
        // A system clock can move backwards while an accepted child is running.
        // Keep the actual captured timestamps and let matching-run identity resolve its schedule.
    }

    static void ValidateReportBinding(CompletionReceipt receipt, byte[] reportBytes)
    {
        if (receipt.ReportLength != reportBytes.LongLength)
            throw new InvalidDataException("Completion report length does not match its receipt.");
        byte[] expected = Convert.FromHexString(receipt.ReportSha256);
        byte[] actual = SHA256.HashData(reportBytes);
        if (!CryptographicOperations.FixedTimeEquals(expected, actual))
            throw new InvalidDataException("Completion report hash does not match its receipt.");
    }

    bool IsIdenticalReceipt(string path, byte[] expectedBytes, CompletionReceipt expected)
    {
        try
        {
            byte[] existingBytes = ReadSimpleBounded(path, MaximumReceiptBytes);
            if (existingBytes.AsSpan().SequenceEqual(expectedBytes)) return true;
            var actual = ParseAndValidateReceipt(existingBytes, Path.GetFileName(path), expected.ReportFileName);
            return SameReceipt(actual, expected);
        }
        catch (Exception ex) when (IsStorageException(ex))
        {
            return false;
        }
    }

    static DirectoryEntry[] ReadDirectoryEntries(
        string path, string label, ref bool scanIncomplete, List<string> diagnostics)
    {
        var entries = new List<DirectoryEntry>(MaximumDirectoryEntries);
        try
        {
            if (!Directory.Exists(path)) return Array.Empty<DirectoryEntry>();
            EnsurePlainDirectory(path);
            using var iterator = Directory.EnumerateFileSystemEntries(path, "*", SearchOption.TopDirectoryOnly).GetEnumerator();
            int examined = 0;
            while (iterator.MoveNext())
            {
                if (examined == MaximumDirectoryEntries)
                {
                    scanIncomplete = true;
                    AddDiagnostic(diagnostics, $"The {label} scan reached the {MaximumDirectoryEntries}-entry limit.");
                    break;
                }
                examined++;
                string fullPath = Path.GetFullPath(iterator.Current);
                if (!IsDirectChild(path, fullPath))
                {
                    scanIncomplete = true;
                    AddDiagnostic(diagnostics, $"The {label} directory contained an entry outside its expected directory.");
                    continue;
                }
                string name = Path.GetFileName(fullPath);
                var attributes = File.GetAttributes(fullPath);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    scanIncomplete = true;
                    AddDiagnostic(diagnostics, $"Ignored a reparse-point entry in {label}: {name}");
                    entries.Add(new DirectoryEntry(name, fullPath, false));
                    continue;
                }
                bool isFile = (attributes & FileAttributes.Directory) == 0;
                entries.Add(new DirectoryEntry(name, fullPath, isFile));
            }
            return entries.ToArray();
        }
        catch (Exception ex) when (IsStorageException(ex))
        {
            scanIncomplete = true;
            AddDiagnostic(diagnostics, $"The {label} directory could not be scanned: {ex.Message}");
            return entries.ToArray();
        }
    }

    static byte[] ReadContentBounded(
        string path,
        int maximumFileBytes,
        long remainingScanBytes,
        ref long bytesRead,
        out bool scanBudgetExhausted)
    {
        scanBudgetExhausted = false;
        if (remainingScanBytes <= 0)
        {
            scanBudgetExhausted = true;
            return Array.Empty<byte>();
        }
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete, 64 * 1024, FileOptions.SequentialScan);
        if (stream.Length > maximumFileBytes)
            throw new ScanIncompleteException($"File exceeds the {maximumFileBytes}-byte read limit.");
        if (stream.Length > remainingScanBytes)
        {
            scanBudgetExhausted = true;
            return Array.Empty<byte>();
        }

        using var memory = new MemoryStream((int)Math.Min(stream.Length, 64 * 1024));
        byte[] chunk = new byte[64 * 1024];
        long permittedBytes = Math.Min(maximumFileBytes, remainingScanBytes);
        while (memory.Length < permittedBytes)
        {
            int requested = (int)Math.Min(chunk.Length, permittedBytes - memory.Length);
            int count = stream.Read(chunk, 0, requested);
            if (count == 0) break;
            memory.Write(chunk, 0, count);
            bytesRead += count;
        }

        if (stream.Position < stream.Length)
        {
            scanBudgetExhausted = bytesRead >= remainingScanBytes;
            throw new ScanIncompleteException(scanBudgetExhausted
                ? "The scan content budget was reached while reading a file."
                : $"File exceeds the {maximumFileBytes}-byte read limit.");
        }
        return memory.ToArray();
    }

    static byte[] ReadSimpleBounded(string path, int maximumBytes)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
            16 * 1024, FileOptions.SequentialScan);
        return ReadStreamBounded(stream, maximumBytes);
    }

    internal static byte[] ReadStreamBounded(Stream stream, int maximumBytes)
    {
        if (stream.Length > maximumBytes) throw new InvalidDataException("File exceeds its read limit.");
        using var memory = new MemoryStream((int)Math.Min(stream.Length, 16 * 1024));
        byte[] chunk = new byte[16 * 1024];
        while (memory.Length <= maximumBytes)
        {
            int remaining = checked(maximumBytes + 1 - (int)memory.Length);
            int count = stream.Read(chunk, 0, Math.Min(chunk.Length, remaining));
            if (count == 0) break;
            memory.Write(chunk, 0, count);
        }
        if (memory.Length > maximumBytes) throw new InvalidDataException("File exceeds its read limit.");
        return memory.ToArray();
    }

    static void RequireProperties(JsonElement root, params string[] names)
    {
        foreach (string name in names)
            if (!root.TryGetProperty(name, out _))
                throw new InvalidDataException($"Completion record is missing required field {name}.");
    }

    static bool IsCanonicalReceiptFileName(string name) =>
        name.EndsWith(".completion.json", StringComparison.Ordinal) &&
        TryGetReceiptReportName(name, out _);

    static bool TryGetReceiptReportName(string receiptName, out string reportName)
    {
        reportName = "";
        const string suffix = ".completion.json";
        if (!receiptName.EndsWith(suffix, StringComparison.Ordinal)) return false;
        string stem = receiptName[..^suffix.Length];
        if (stem.Length <= 20) return false;
        int separator = stem.LastIndexOf('-');
        if (separator <= 0) return false;
        string stamp = stem[..separator];
        string runId = stem[(separator + 1)..];
        if (!DateTime.TryParseExact(stamp, FileStampFormat, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out _) ||
            !Guid.TryParseExact(runId, "N", out var parsed) ||
            !string.Equals(parsed.ToString("N"), runId, StringComparison.Ordinal)) return false;
        reportName = stem + ".json";
        return ShadowReviewStore.IsSafeRetainedReportBasename(reportName);
    }

    static bool SameDirectoryIdentity(string value, string expected)
    {
        string normalized = ControlEndpoint.NormalizeDataDirectory(value);
        return string.Equals(normalized, expected,
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    }

    static bool IsSha256(string? value) => value is not null && value.Length == 64 && value.All(Uri.IsHexDigit);

    static bool SameReceipt(CompletionReceipt first, CompletionReceipt second) =>
        first.SchemaVersion == second.SchemaVersion && first.DataDirectory == second.DataDirectory &&
        first.RunId == second.RunId && first.StartedUtc == second.StartedUtc &&
        first.CompletedUtc == second.CompletedUtc && first.InputCutoffUtc == second.InputCutoffUtc &&
        first.ReportFileName == second.ReportFileName && first.ReportLength == second.ReportLength &&
        string.Equals(first.ReportSha256, second.ReportSha256, StringComparison.OrdinalIgnoreCase) &&
        SameSummary(first.Summary, second.Summary);

    static bool SameSummary(ShadowReviewSummary first, ShadowReviewSummary second)
    {
        try
        {
            return JsonSerializer.SerializeToUtf8Bytes(first, JsonOptions)
                .AsSpan().SequenceEqual(JsonSerializer.SerializeToUtf8Bytes(second, JsonOptions));
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            return false;
        }
    }

    static bool SnapshotsEqual(ShadowReviewSnapshot first, ShadowReviewSnapshot second)
    {
        try
        {
            byte[] firstBytes = JsonSerializer.SerializeToUtf8Bytes(first, JsonOptions);
            byte[] secondBytes = JsonSerializer.SerializeToUtf8Bytes(second, JsonOptions);
            return firstBytes.AsSpan().SequenceEqual(secondBytes);
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            return false;
        }
    }

    static void AddDiagnostic(List<string> diagnostics, string detail)
    {
        if (diagnostics.Count < 32 && !diagnostics.Contains(detail, StringComparer.Ordinal)) diagnostics.Add(detail);
    }

    static bool IsRegularFile(string path)
    {
        if (!File.Exists(path)) return false;
        var attributes = File.GetAttributes(path);
        return (attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) == 0;
    }

    static void EnsurePlainDirectory(string path)
    {
        var attributes = File.GetAttributes(path);
        if ((attributes & FileAttributes.Directory) == 0 || (attributes & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("Expected a normal directory without reparse points.");
    }

    static string DirectChildPath(string directory, string name)
    {
        string fullDirectory = Path.GetFullPath(directory);
        string fullPath = Path.GetFullPath(Path.Combine(fullDirectory, name));
        if (!IsDirectChild(fullDirectory, fullPath))
            throw new InvalidDataException("Path is not a direct child of its expected directory.");
        return fullPath;
    }

    static bool IsDirectChild(string directory, string candidate) =>
        string.Equals(Path.GetDirectoryName(Path.GetFullPath(candidate)), Path.GetFullPath(directory), PathComparison);

    static StringComparison PathComparison => OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;

    static bool IsStorageException(Exception ex) =>
        ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or
            JsonException or InvalidDataException or SecurityException;

    internal sealed record CompletionReceipt
    {
        public int SchemaVersion { get; init; }
        public string DataDirectory { get; init; } = "";
        public string RunId { get; init; } = "";
        public DateTimeOffset StartedUtc { get; init; }
        public DateTimeOffset CompletedUtc { get; init; }
        public DateTimeOffset InputCutoffUtc { get; init; }
        public string ReportFileName { get; init; } = "";
        public long ReportLength { get; init; }
        public string ReportSha256 { get; init; } = "";
        public ShadowReviewSummary Summary { get; init; } = new();
    }

    sealed record DirectoryEntry(string Name, string FullPath, bool IsFile);

    sealed record CachedReceipt(
        CompletionReceipt Receipt,
        long ReceiptLength,
        DateTime ReceiptWriteUtc,
        long ReportLength,
        DateTime ReportWriteUtc);

    sealed record ValidatedReceipt(
        CompletionReceipt Receipt,
        string ReceiptPath,
        long ReceiptLength,
        DateTime ReceiptWriteUtc,
        string ReportPath,
        long ReportLength,
        DateTime ReportWriteUtc);

    sealed class ScanIncompleteException(string message) : IOException(message);
}
