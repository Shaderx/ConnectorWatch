using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Security;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace ConnectorWatch;

/// <summary>Runs an isolated weekly evaluator and stores compact, read-only history.</summary>
public sealed class ShadowReviewScheduler : IDisposable
{
    public static readonly TimeSpan ReviewPeriod = TimeSpan.FromDays(7);
    public static readonly TimeSpan FailureRetryPeriod = TimeSpan.FromHours(6);
    public static readonly TimeSpan EvaluationTimeout = TimeSpan.FromMinutes(15);

    const int MaximumReportBytes = 16 * 1024 * 1024;
    const int MaximumCapturedOutputCharacters = 64 * 1024;
    static readonly TimeSpan DefaultPollPeriod = TimeSpan.FromMinutes(1);

    readonly string suppliedDataDirectory;
    readonly Action<string, Exception?>? log;
    readonly Func<DateTimeOffset> utcNow;
    readonly Func<ShadowReviewProcessRequest, CancellationToken, Task<ShadowReviewProcessResult>> runChild;
    readonly TimeSpan pollPeriod;
    readonly TimeSpan timeout;
    readonly object lifetimeGate = new();

    CancellationTokenSource? lifetime;
    Task? worker;
    FileStream? schedulerLock;
    ShadowReviewSnapshot? snapshot;
    string? dataDirectory;
    string? directory;
    string? statePath;
    string? reportsDirectory;
    bool stateDirty;

    public ShadowReviewScheduler(string dataDirectory, Action<string, Exception?>? log = null)
        : this(dataDirectory, null, log, static () => DateTimeOffset.UtcNow,
            RunEvaluatorChildAsync, DefaultPollPeriod, EvaluationTimeout)
    {
    }

    internal ShadowReviewScheduler(string dataDirectory, string? isolatedStoreRoot,
        Action<string, Exception?>? log, Func<DateTimeOffset> utcNow,
        Func<ShadowReviewProcessRequest, CancellationToken, Task<ShadowReviewProcessResult>> runChild,
        TimeSpan pollPeriod, TimeSpan timeout)
    {
        suppliedDataDirectory = dataDirectory;
        isolatedStoreOverride = isolatedStoreRoot;
        this.log = log;
        this.utcNow = utcNow ?? throw new ArgumentNullException(nameof(utcNow));
        this.runChild = runChild ?? throw new ArgumentNullException(nameof(runChild));
        if (pollPeriod <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(pollPeriod));
        if (timeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(timeout));
        this.pollPeriod = pollPeriod;
        this.timeout = timeout;
    }

    readonly string? isolatedStoreOverride;

    /// <summary>Starts the background worker. Startup failures are logged and never escape to sampling.</summary>
    public void Start(CancellationToken cancellationToken)
    {
        lock (lifetimeGate)
        {
            if (worker is not null) return;
            try
            {
                lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                worker = Task.Run(() => RunLoopAsync(lifetime.Token));
            }
            catch (Exception ex)
            {
                SafeLog("Could not start the offline shadow review scheduler.", ex);
                lifetime?.Dispose();
                lifetime = null;
            }
        }
    }

    public void Dispose()
    {
        Task? running;
        lock (lifetimeGate)
        {
            lifetime?.Cancel();
            running = worker;
        }

        if (running is not null)
        {
            try { running.GetAwaiter().GetResult(); }
            catch (Exception ex) { SafeLog("Shadow review scheduler stopped with an error.", ex); }
        }

        lock (lifetimeGate)
        {
            schedulerLock?.Dispose();
            schedulerLock = null;
            lifetime?.Dispose();
            lifetime = null;
            worker = null;
        }
    }

    async Task RunLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            try { ResolvePaths(); }
            catch (Exception ex)
            {
                SafeLog("Could not resolve the offline shadow review store.", ex);
                return;
            }

            while (!cancellationToken.IsCancellationRequested)
            {
                bool cycleFailed = false;
                try
                {
                    if (schedulerLock is null)
                    {
                        if (!TryAcquireSchedulerLock())
                        {
                            await DelayAsync(pollPeriod, cancellationToken).ConfigureAwait(false);
                            continue;
                        }
                        LoadInitialState();
                    }

                    if (stateDirty) SaveSnapshot();
                    DateTimeOffset now = UtcNow();
                    if (snapshot is null)
                    {
                        await DelayAsync(pollPeriod, cancellationToken).ConfigureAwait(false);
                        continue;
                    }

                    if (snapshot.Status == "RUNNING")
                    {
                        // A RUNNING record can only be left by a stopped or crashed daemon.
                        // Preserve its retry deadline and expose the abandoned run as failed.
                        snapshot = snapshot with
                        {
                            Status = "FAILED",
                            Detail = "The previous daemon stopped during this review. The persisted retry deadline remains in effect.",
                            ActiveRunId = null,
                            UpdatedUtc = now,
                        };
                        SaveSnapshot();
                    }

                    DateTimeOffset dueAt = NextAttempt(snapshot);
                    if (dueAt <= now)
                    {
                        await RunReviewAsync(cancellationToken).ConfigureAwait(false);
                        continue;
                    }

                    TimeSpan untilDue = dueAt - now;
                    await DelayAsync(untilDue < pollPeriod ? untilDue : pollPeriod, cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    cycleFailed = true;
                    SafeLog("Offline shadow review scheduler encountered a recoverable cycle error.", ex);
                    SetFailureInMemory(ex, UtcNow());
                    try { if (snapshot is not null) SaveSnapshot(); }
                    catch (Exception persistError) { SafeLog("Could not persist the shadow review retry state.", persistError); }
                }

                if (cycleFailed)
                    await DelayAsync(pollPeriod, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Shutdown is handled in the finally block, after the child has stopped.
        }
        catch (Exception ex)
        {
            SafeLog("Offline shadow review scheduler encountered an error.", ex);
        }
        finally
        {
            if (snapshot is not null && statePath is not null)
            {
                try
                {
                    bool preserveFailure = snapshot.Status == "FAILED";
                    snapshot = snapshot with
                    {
                        Status = preserveFailure ? "FAILED" : "STOPPED",
                        Detail = preserveFailure ? snapshot.Detail :
                            snapshot.Status == "STOPPED" ? snapshot.Detail : "Offline shadow review scheduler stopped.",
                        ActiveRunId = null,
                        UpdatedUtc = UtcNow(),
                    };
                    SaveSnapshot();
                }
                catch (Exception ex) { SafeLog("Could not save stopped shadow review state.", ex); }
            }

            schedulerLock?.Dispose();
            schedulerLock = null;
        }
    }

    void ResolvePaths()
    {
        dataDirectory = ControlEndpoint.NormalizeDataDirectory(suppliedDataDirectory);
        directory = isolatedStoreOverride is null
            ? ShadowReviewStore.DirectoryFor(dataDirectory)
            : Path.GetFullPath(isolatedStoreOverride);
        if (IsWithin(directory, dataDirectory))
            throw new InvalidOperationException("The shadow review store cannot be inside the telemetry directory.");

        statePath = Path.Combine(directory, "state.json");
        reportsDirectory = Path.Combine(directory, "reports");
    }

    bool TryAcquireSchedulerLock()
    {
        try
        {
            Directory.CreateDirectory(directory!);
            schedulerLock = new FileStream(Path.Combine(directory!, "review.lock"), FileMode.OpenOrCreate,
                FileAccess.ReadWrite, FileShare.None, 1, FileOptions.None);
            return true;
        }
        catch (IOException ex)
        {
            SafeLog("Another shadow review scheduler owns the review lock; this scheduler will retry.", ex);
            return false;
        }
        catch (Exception ex)
        {
            SafeLog("Could not acquire the shadow review lock; this scheduler will retry.", ex);
            return false;
        }
    }

    void LoadInitialState()
    {
        var loaded = ShadowReviewStore.ReadAtPath(dataDirectory!, statePath!, out string? readError);
        DateTimeOffset now = UtcNow();
        if (loaded is null)
        {
            if (!string.IsNullOrEmpty(readError)) SafeLog("Previous shadow review state could not be read.", new InvalidDataException(readError));
            snapshot = new ShadowReviewSnapshot
            {
                DataDirectory = dataDirectory!,
                Status = "WAITING",
                Detail = string.IsNullOrEmpty(readError)
                    ? "Waiting for the first offline shadow review."
                    : "Previous review state was invalid. A new review will start now.",
                UpdatedUtc = now,
            };
            SaveSnapshot();
            return;
        }

        snapshot = loaded;
        if (!string.Equals(snapshot.DataDirectory, dataDirectory,
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
        {
            snapshot = snapshot with { DataDirectory = dataDirectory! };
            SaveSnapshot();
        }
    }

    async Task RunReviewAsync(CancellationToken shutdown)
    {
        ShadowReviewSnapshot previous = snapshot!;
        DateTimeOffset started = UtcNow();
        DateTimeOffset cutoff = new(started.UtcDateTime.Date, TimeSpan.Zero);
        string runId = Guid.NewGuid().ToString("N");
        string stamp = started.UtcDateTime.ToString("yyyyMMdd'T'HHmmssfff'Z'", CultureInfo.InvariantCulture);
        string fileName = stamp + "-" + runId + ".json";
        string reportPath = Path.Combine(reportsDirectory!, fileName);
        var request = new ShadowReviewProcessRequest(dataDirectory!, cutoff, reportPath, runId);

        snapshot = previous with
        {
            Status = "RUNNING",
            Detail = "An offline shadow review is running against retained telemetry.",
            UpdatedUtc = started,
            StartedUtc = started,
            NextAttemptUtc = started + FailureRetryPeriod,
            ActiveRunId = runId,
        };
        SaveSnapshot();

        try
        {
            Directory.CreateDirectory(reportsDirectory!);
            using var timeoutSource = new CancellationTokenSource(timeout);
            using var linkedSource = CancellationTokenSource.CreateLinkedTokenSource(shutdown,
                timeoutSource.Token);
            ShadowReviewProcessResult processResult;
            try
            {
                processResult = await runChild(request, linkedSource.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (shutdown.IsCancellationRequested)
            {
                snapshot = snapshot! with
                {
                    Status = "STOPPED",
                    Detail = "Daemon shutdown stopped the offline shadow review.",
                    ActiveRunId = null,
                    UpdatedUtc = UtcNow(),
                };
                SaveSnapshot();
                throw;
            }
            catch (OperationCanceledException) when (timeoutSource.IsCancellationRequested)
            {
                throw new TimeoutException($"Offline shadow evaluation exceeded {timeout.TotalMinutes:0.#} minutes.");
            }

            if (processResult.ExitCode != 0)
                throw new InvalidOperationException(FormatProcessFailure(processResult));

            byte[] reportBytes = ReadReportBounded(reportPath, MaximumReportBytes);
            ShadowReviewSummary summary = ParseReport(reportBytes, runId, fileName, UtcNow(), cutoff);
            if (summary.State is "TRUNCATED_INPUT" or "INVALID_INPUT")
                throw new InvalidDataException($"Evaluator returned {summary.State}; the full report was retained and the review will retry.");
            if (summary.State is not ("SUFFICIENT" or "INSUFFICIENT_DATA"))
                throw new InvalidDataException($"Evaluator returned unsupported state {summary.State}.");

            summary = AddReviewWarnings(summary, snapshot!.History);
            DateTimeOffset completed = summary.CompletedUtc;
            var history = new[] { summary }.Concat(snapshot!.History)
                .Take(12).ToArray();
            snapshot = snapshot with
            {
                Status = "COMPLETED",
                Detail = summary.State == "INSUFFICIENT_DATA"
                    ? "Review completed. The selected cohort does not yet meet the evaluator's coverage requirements."
                    : "Review completed successfully.",
                UpdatedUtc = completed,
                StartedUtc = started,
                NextAttemptUtc = completed + ReviewPeriod,
                LastSuccessUtc = completed,
                ActiveRunId = null,
                History = history,
            };
            SaveSnapshot();
        }
        catch (OperationCanceledException) when (shutdown.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            DateTimeOffset failed = UtcNow();
            snapshot = previous with
            {
                Status = "FAILED",
                Detail = ex.Message,
                UpdatedUtc = failed,
                StartedUtc = started,
                NextAttemptUtc = failed + FailureRetryPeriod,
                ActiveRunId = null,
            };
            try { SaveSnapshot(); }
            catch (Exception persistError)
            {
                SafeLog("Could not persist the failed review state; it will be retried.", persistError);
            }
            SafeLog("Offline shadow review failed; the previous completed result remains available.", ex);
        }
    }

    ShadowReviewSummary AddReviewWarnings(ShadowReviewSummary summary, ShadowReviewSummary[] priorHistory)
    {
        var warnings = summary.Warnings.ToList();
        if (summary.RejectedRows > 0 || summary.InvalidRows > 0)
            warnings.Add($"Input exclusions: {summary.RejectedRows} rejected rows and {summary.InvalidRows} invalid rows.");

        if (priorHistory.Length > 0)
        {
            var prior = priorHistory[0];
            if (summary.SourceRows == prior.SourceRows && summary.CohortKey == prior.CohortKey &&
                summary.TestRows == prior.TestRows && summary.FairHeldoutRows == prior.FairHeldoutRows)
                warnings.Add("This review completed without new source or selected-cohort heldout rows since the previous review.");
        }

        return summary with { Warnings = warnings.Distinct(StringComparer.Ordinal).ToArray() };
    }

    void SaveSnapshot()
    {
        stateDirty = true;
        snapshot = ShadowReviewStore.WriteAtomic(statePath!, snapshot!);
        stateDirty = false;
    }

    void SetFailureInMemory(Exception exception, DateTimeOffset failed)
    {
        if (snapshot is null) return;
        snapshot = snapshot with
        {
            Status = "FAILED",
            Detail = exception.Message,
            UpdatedUtc = failed,
            NextAttemptUtc = failed + FailureRetryPeriod,
            ActiveRunId = null,
        };
        stateDirty = true;
    }

    DateTimeOffset NextAttempt(ShadowReviewSnapshot state)
    {
        if (state.NextAttemptUtc is DateTimeOffset next) return next.ToUniversalTime();
        if (state.Status == "COMPLETED")
            return (state.LastSuccessUtc ?? state.History.FirstOrDefault()?.CompletedUtc ?? UtcNow()) + ReviewPeriod;
        if (state.Status == "FAILED") return UtcNow() + FailureRetryPeriod;
        return DateTimeOffset.MinValue;
    }

    DateTimeOffset UtcNow() => utcNow().ToUniversalTime();

    static async Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        if (delay <= TimeSpan.Zero) return;
        await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
    }

    void SafeLog(string message, Exception? exception)
    {
        try { log?.Invoke(message, exception); }
        catch { }
    }

    static string FormatProcessFailure(ShadowReviewProcessResult result)
    {
        string detail = string.Join("\n", new[] { result.StandardError, result.StandardOutput }
            .Where(x => !string.IsNullOrWhiteSpace(x)));
        if (detail.Length > 8 * 1024) detail = detail[..(8 * 1024)];
        return string.IsNullOrWhiteSpace(detail)
            ? $"Offline shadow evaluator exited with code {result.ExitCode}."
            : $"Offline shadow evaluator exited with code {result.ExitCode}: {detail}";
    }

    static async Task<ShadowReviewProcessResult> RunEvaluatorChildAsync(
        ShadowReviewProcessRequest request, CancellationToken cancellationToken)
    {
        string processPath = Environment.ProcessPath
            ?? throw new InvalidOperationException("The current process path is not available.");
        var start = new ProcessStartInfo
        {
            FileName = processPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        string executableName = Path.GetFileNameWithoutExtension(processPath);
        if (executableName.Equals("dotnet", StringComparison.OrdinalIgnoreCase))
        {
            string assemblyPath = Assembly.GetEntryAssembly()?.Location ??
                typeof(ShadowReviewScheduler).Assembly.Location;
            if (string.IsNullOrWhiteSpace(assemblyPath) || !File.Exists(assemblyPath))
                throw new FileNotFoundException("The existing ConnectorWatch assembly could not be located.", assemblyPath);
            start.ArgumentList.Add(assemblyPath);
        }

        start.ArgumentList.Add("--evaluate-prediction");
        start.ArgumentList.Add(request.DataDirectory);
        start.ArgumentList.Add("--as-of");
        start.ArgumentList.Add(request.InputCutoffUtc.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        start.ArgumentList.Add("--output");
        start.ArgumentList.Add(request.ReportPath);

        using var process = new Process { StartInfo = start, EnableRaisingEvents = true };
        if (!process.Start()) throw new InvalidOperationException("The offline shadow evaluator process did not start.");

        Task<string> standardOutput = ReadBoundedOutputAsync(process.StandardOutput,
            MaximumCapturedOutputCharacters, cancellationToken);
        Task<string> standardError = ReadBoundedOutputAsync(process.StandardError,
            MaximumCapturedOutputCharacters, cancellationToken);
        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
            catch (System.ComponentModel.Win32Exception) { }
            try { await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false); }
            catch (InvalidOperationException) { }
            throw;
        }

        await Task.WhenAll(standardOutput, standardError).ConfigureAwait(false);
        return new ShadowReviewProcessResult(process.ExitCode, standardOutput.Result, standardError.Result);
    }

    internal static Task<ShadowReviewProcessResult> RunEvaluatorChildForTestAsync(
        ShadowReviewProcessRequest request, CancellationToken cancellationToken) =>
        RunEvaluatorChildAsync(request, cancellationToken);

    static async Task<string> ReadBoundedOutputAsync(StreamReader reader, int maximumCharacters,
        CancellationToken cancellationToken)
    {
        var builder = new StringBuilder(Math.Min(maximumCharacters, 4096));
        char[] buffer = new char[4096];
        bool truncated = false;
        while (true)
        {
            int count = await reader.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
            if (count == 0) break;
            int available = maximumCharacters - builder.Length;
            if (available > 0) builder.Append(buffer, 0, Math.Min(count, available));
            if (count > available) truncated = true;
        }
        if (truncated) builder.Append("\n[output truncated]");
        return builder.ToString();
    }

    static byte[] ReadReportBounded(string path, int maximumBytes)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            64 * 1024, FileOptions.SequentialScan);
        using var memory = new MemoryStream(Math.Min((int)Math.Min(stream.Length, maximumBytes), 64 * 1024));
        byte[] buffer = new byte[64 * 1024];
        while (true)
        {
            int remaining = maximumBytes + 1 - checked((int)memory.Length);
            if (remaining <= 0) break;
            int read = stream.Read(buffer, 0, Math.Min(buffer.Length, remaining));
            if (read == 0) break;
            memory.Write(buffer, 0, read);
        }
        if (memory.Length > maximumBytes)
            throw new InvalidDataException($"Evaluator report exceeds the {maximumBytes}-byte read limit.");
        return memory.ToArray();
    }

    static ShadowReviewSummary ParseReport(byte[] bytes, string runId, string reportFileName,
        DateTimeOffset completedUtc, DateTimeOffset expectedCutoffUtc)
    {
        using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions
        {
            AllowTrailingCommas = false,
            CommentHandling = JsonCommentHandling.Disallow,
            MaxDepth = 64,
        });
        var root = Object(document.RootElement, "report");
        string state = RequiredString(root, "State");
        string conclusion = RequiredString(root, "Conclusion");
        DateTimeOffset cutoff = RequiredDateTime(root, "InputCutoffUtc");
        if (cutoff.ToUniversalTime() != expectedCutoffUtc.ToUniversalTime())
            throw new InvalidDataException("Evaluator report cutoff does not match the launched review cutoff.");
        string fingerprint = RequiredString(root, "FrozenCutoffFingerprint");
        string modelIdentity = RequiredString(root, "ModelIdentity");
        string configurationIdentity = RequiredString(root, "ConfigurationIdentity");
        string? winner = OptionalString(root, "WinnerModel");

        var coverage = Object(Required(root, "Coverage"), "Coverage");
        string cohortKey = RequiredString(coverage, "CohortKey");
        int supportedDays = RequiredInt32(coverage, "SupportedDays");
        int testDays = RequiredInt32(coverage, "TestDays");
        int fairHeldoutRows = RequiredInt32(coverage, "FairHeldoutRows");
        int testRows = RequiredInt32(coverage, "TestRows");
        string[] trainingDays = RequiredStringArray(coverage, "TrainingDayKeys");
        string[] calibrationDays = RequiredStringArray(coverage, "CalibrationDayKeys");
        string[] testDayKeys = RequiredStringArray(coverage, "TestDayKeys");

        var inventory = Object(Required(root, "InputInventory"), "InputInventory");
        long sourceRows = RequiredInt64(inventory, "RowsRead");
        long rejectedRows = RequiredInt64(inventory, "RejectedRows");
        long invalidRows = RequiredInt64(inventory, "InvalidRows");

        var selectedTestPartitions = RequiredArray(root, "DayPartitions")
            .Select(x => Object(x, "DayPartitions entry"))
            .Where(x => RequiredString(x, "CohortKey") == cohortKey &&
                testDayKeys.Contains(RequiredString(x, "UtcDate"), StringComparer.Ordinal))
            .ToArray();
        double? powerMinimum = Minimum(selectedTestPartitions.Select(x => OptionalFiniteDouble(x, "PowerMinimumW")));
        double? powerMaximum = Maximum(selectedTestPartitions.Select(x => OptionalFiniteDouble(x, "PowerMaximumW")));
        double? currentMinimum = Minimum(selectedTestPartitions.Select(x => OptionalFiniteDouble(x, "CurrentMinimumA")));
        double? currentMaximum = Maximum(selectedTestPartitions.Select(x => OptionalFiniteDouble(x, "CurrentMaximumA")));
        double? temperatureMinimum = Minimum(selectedTestPartitions.Select(x => OptionalFiniteDouble(x, "TemperatureMinimumC")));
        double? temperatureMaximum = Maximum(selectedTestPartitions.Select(x => OptionalFiniteDouble(x, "TemperatureMaximumC")));
        int temperatureMinutes = selectedTestPartitions.Sum(x => RequiredInt32(x, "TemperatureValidMinutes"));

        var models = RequiredArray(root, "Models").Select(x =>
        {
            var model = Object(x, "Models entry");
            bool hasFairMetrics = model.TryGetProperty("FairMetrics", out var fairMetrics) &&
                fairMetrics.ValueKind != JsonValueKind.Null;
            double? fairMae = null;
            double? fairRmse = null;
            int fairRows = 0;
            if (hasFairMetrics)
            {
                var metrics = Object(fairMetrics, "FairMetrics");
                fairRows = RequiredInt32(metrics, "SampleCount");
                fairMae = RequiredFiniteDouble(metrics, "MeanAbsoluteErrorV");
                fairRmse = RequiredFiniteDouble(metrics, "RootMeanSquareErrorV");
            }
            return new ShadowReviewModelSummary
            {
                Name = RequiredString(model, "Name"),
                IsAvailable = RequiredBoolean(model, "IsAvailable"),
                EligibleForRanking = RequiredBoolean(model, "EligibleForRanking"),
                FairRows = fairRows,
                OutOfEnvelopeRows = RequiredInt32(model, "OutOfEnvelopeRows"),
                FairMaeV = fairMae,
                FairRmseV = fairRmse,
                Detail = RequiredString(model, "Detail"),
            };
        }).ToArray();

        var advisory = Object(Required(root, "Advisory"), "Advisory");
        string advisoryModel = RequiredString(advisory, "Model");
        int transitions = RequiredInt32(advisory, "AdvisoryTransitions");
        double? transitionsPerHour = OptionalFiniteDouble(advisory, "TransitionsPerObservedHour");
        var synthetic = RequiredArray(advisory, "SyntheticFaults").Select(x =>
        {
            var fault = Object(x, "SyntheticFaults entry");
            return new ShadowReviewSyntheticSummary
            {
                Label = RequiredString(fault, "Label"),
                ScenarioIdentity = RequiredString(fault, "ScenarioIdentity"),
                State = RequiredString(fault, "ScenarioState"),
                OnsetUtc = OptionalDateTime(fault, "OnsetTimestampUtc"),
                WindowEndUtc = OptionalDateTime(fault, "WindowEndTimestampUtc"),
                DetectionLatencySeconds = OptionalFiniteDouble(fault, "DetectionLatencySeconds"),
                AttributableTransitions = RequiredInt32(fault, "AttributableTransitions"),
                Detail = RequiredString(fault, "Detail"),
            };
        }).ToArray();

        string[] warnings = RequiredStringArray(root, "Warnings");
        DateTimeOffset? newestTestDay = testDayKeys.Select(ParseDayOrDefault)
            .Where(x => x != default).Cast<DateTimeOffset?>().Max();
        if (newestTestDay is DateTimeOffset latest)
        {
            DateTimeOffset cutoffDay = new(cutoff.UtcDateTime.Date, TimeSpan.Zero);
            TimeSpan age = cutoffDay - latest;
            if (age >= TimeSpan.FromDays(7))
                warnings = warnings.Append($"The newest selected-cohort test day is {(int)age.TotalDays} days behind the review cutoff.")
                    .Distinct(StringComparer.Ordinal).ToArray();
        }

        return new ShadowReviewSummary
        {
            RunId = runId,
            CompletedUtc = completedUtc,
            InputCutoffUtc = cutoff,
            State = state,
            Conclusion = conclusion,
            WinnerModel = winner,
            CohortKey = cohortKey,
            ModelIdentity = modelIdentity,
            ConfigurationIdentity = configurationIdentity,
            FrozenCutoffFingerprint = fingerprint,
            SupportedDays = supportedDays,
            TestDays = testDays,
            FairHeldoutRows = fairHeldoutRows,
            TestRows = testRows,
            TrainingDays = trainingDays,
            CalibrationDays = calibrationDays,
            AdvisoryTransitions = transitions,
            AdvisoryTransitionsPerObservedHour = transitionsPerHour,
            PowerMinimumW = powerMinimum,
            PowerMaximumW = powerMaximum,
            CurrentMinimumA = currentMinimum,
            CurrentMaximumA = currentMaximum,
            GpuTemperatureMinimumC = temperatureMinimum,
            GpuTemperatureMaximumC = temperatureMaximum,
            TemperatureValidMinutes = temperatureMinutes,
            SourceRows = sourceRows,
            RejectedRows = rejectedRows,
            InvalidRows = invalidRows,
            Models = models,
            SyntheticFaults = synthetic,
            Warnings = warnings,
            ReportFileName = reportFileName,
        };

        static DateTimeOffset ParseDayOrDefault(string day) => DateOnly.TryParseExact(day, "yyyy-MM-dd",
            CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            ? new DateTimeOffset(date.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero)
            : default;
    }

    static double? Minimum(IEnumerable<double?> values)
    {
        var finite = values.Where(x => x is double number && double.IsFinite(number)).Select(x => x!.Value).ToArray();
        return finite.Length == 0 ? null : finite.Min();
    }

    static double? Maximum(IEnumerable<double?> values)
    {
        var finite = values.Where(x => x is double number && double.IsFinite(number)).Select(x => x!.Value).ToArray();
        return finite.Length == 0 ? null : finite.Max();
    }

    static JsonElement Object(JsonElement value, string name) => value.ValueKind == JsonValueKind.Object
        ? value
        : throw new InvalidDataException($"Evaluator report field {name} must be an object.");

    static JsonElement Required(JsonElement parent, string name) => parent.TryGetProperty(name, out var value)
        ? value
        : throw new InvalidDataException($"Evaluator report is missing required field {name}.");

    static string RequiredString(JsonElement parent, string name)
    {
        var value = Required(parent, name);
        if (value.ValueKind != JsonValueKind.String)
            throw new InvalidDataException($"Evaluator report field {name} must be a string.");
        return value.GetString() ?? throw new InvalidDataException($"Evaluator report field {name} cannot be null.");
    }

    static string? OptionalString(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out var value)) return null;
        return value.ValueKind switch
        {
            JsonValueKind.Null => null,
            JsonValueKind.String => value.GetString(),
            _ => throw new InvalidDataException($"Evaluator report field {name} must be a string or null."),
        };
    }

    static string[] RequiredStringArray(JsonElement parent, string name) => RequiredArray(parent, name)
        .Select(x => x.ValueKind == JsonValueKind.String
            ? x.GetString() ?? throw new InvalidDataException($"Evaluator report array {name} contains null.")
            : throw new InvalidDataException($"Evaluator report array {name} must contain strings."))
        .ToArray();

    static JsonElement[] RequiredArray(JsonElement parent, string name)
    {
        var value = Required(parent, name);
        if (value.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException($"Evaluator report field {name} must be an array.");
        return value.EnumerateArray().ToArray();
    }

    static int RequiredInt32(JsonElement parent, string name)
    {
        var value = Required(parent, name);
        if (!value.TryGetInt32(out int result) || result < 0)
            throw new InvalidDataException($"Evaluator report field {name} must be a non-negative integer.");
        return result;
    }

    static long RequiredInt64(JsonElement parent, string name)
    {
        var value = Required(parent, name);
        if (!value.TryGetInt64(out long result) || result < 0)
            throw new InvalidDataException($"Evaluator report field {name} must be a non-negative integer.");
        return result;
    }

    static bool RequiredBoolean(JsonElement parent, string name)
    {
        var value = Required(parent, name);
        if (value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            throw new InvalidDataException($"Evaluator report field {name} must be a boolean.");
        return value.GetBoolean();
    }

    static double? OptionalFiniteDouble(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out var value)) return null;
        if (value.ValueKind == JsonValueKind.Null) return null;
        if (!value.TryGetDouble(out double number) || !double.IsFinite(number))
            throw new InvalidDataException($"Evaluator report field {name} must be a finite number or null.");
        return number;
    }

    static double RequiredFiniteDouble(JsonElement parent, string name) =>
        OptionalFiniteDouble(parent, name) is double value
            ? value
            : throw new InvalidDataException($"Evaluator report field {name} must be present and finite.");

    static DateTimeOffset RequiredDateTime(JsonElement parent, string name) => OptionalDateTime(parent, name)
        ?? throw new InvalidDataException($"Evaluator report field {name} must be a timestamp.");

    static DateTimeOffset? OptionalDateTime(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out var value)) return null;
        if (value.ValueKind == JsonValueKind.Null) return null;
        if (value.ValueKind != JsonValueKind.String ||
            !value.TryGetDateTimeOffset(out var timestamp))
            throw new InvalidDataException($"Evaluator report field {name} must be a timestamp or null.");
        return timestamp.ToUniversalTime();
    }

    static bool IsWithin(string path, string directory)
    {
        string relative = Path.GetRelativePath(directory, path);
        return !Path.IsPathRooted(relative) && (relative == "." || relative != ".." &&
            !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) &&
            !relative.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal));
    }
}

internal sealed record ShadowReviewProcessRequest(
    string DataDirectory,
    DateTimeOffset InputCutoffUtc,
    string ReportPath,
    string RunId);

internal sealed record ShadowReviewProcessResult(int ExitCode, string StandardOutput, string StandardError);
