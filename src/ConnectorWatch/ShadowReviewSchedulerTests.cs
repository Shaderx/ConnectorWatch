using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace ConnectorWatch;

public static class ShadowReviewSchedulerTests
{
    public static void Run()
    {
        int checks = 0;
        void Check(bool condition, string name)
        {
            if (!condition) throw new Exception("FAILED shadow review scheduler: " + name);
            checks++;
        }

        string root = Path.Combine(Path.GetTempPath(), "ConnectorWatch-shadow-review-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            StoreReaderTests(root, Check);
            SuccessRetryAndRestartTests(root, Check);
            ReportValidationTests(root, Check);
            AbandonedRunTests(root, Check);
            SingleWriterAndCancellationTests(root, Check);
            StoreInsideTelemetryTests(root, Check);
            ChildProcessIntegrationTest(root, Check);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
        Console.WriteLine($"PASS: {checks} shadow review scheduler checks.");
    }

    static void StoreReaderTests(string root, Action<bool, string> check)
    {
        string folder = Path.Combine(root, "store-reader");
        string data = Path.Combine(folder, "telemetry");
        string store = Path.Combine(folder, "review-store");
        Directory.CreateDirectory(data);
        Directory.CreateDirectory(store);
        string statePath = Path.Combine(store, "state.json");
        DateTimeOffset now = Utc(2026, 9, 28, 12);
        var summary = CreateSummary(now, "baseline.json");
        var snapshot = new ShadowReviewSnapshot
        {
            DataDirectory = ControlEndpoint.NormalizeDataDirectory(data),
            Status = "COMPLETED",
            Detail = "Stored test state.",
            UpdatedUtc = now,
            LastSuccessUtc = now,
            NextAttemptUtc = now + ShadowReviewScheduler.ReviewPeriod,
            History = [summary],
        };

        check(ShadowReviewStore.ReadAtPath(data, statePath, out string? missingError) is null && missingError is null,
            "missing isolated state is a clean empty result");
        ShadowReviewStore.WriteAtomic(statePath, snapshot);
        var loaded = ShadowReviewStore.ReadAtPath(data, statePath, out string? readError);
        check(loaded is not null && readError is null && loaded.History.Length == 1 &&
              loaded.History[0].ReportFileName == "baseline.json", "valid state and history round trip");

        var wrongIdentity = ShadowReviewStore.ReadAtPath(Path.Combine(folder, "other-data"), statePath,
            out string? identityError);
        check(wrongIdentity is null && identityError?.Contains("different telemetry directory", StringComparison.Ordinal) == true,
            "reader rejects state for another data directory");

        string original = File.ReadAllText(statePath);
        JsonObject missingNested = JsonNode.Parse(original)!.AsObject();
        missingNested["History"]![0]!["Models"]![0]!.AsObject().Remove("Detail");
        File.WriteAllText(statePath, missingNested.ToJsonString());
        var invalidNested = ShadowReviewStore.ReadAtPath(data, statePath, out string? nestedError);
        check(invalidNested is null && nestedError?.Contains("Detail", StringComparison.Ordinal) == true,
            "reader rejects missing nested required fields");

        JsonObject wrongSchema = JsonNode.Parse(original)!.AsObject();
        wrongSchema["SchemaVersion"] = 2;
        File.WriteAllText(statePath, wrongSchema.ToJsonString());
        var invalidSchema = ShadowReviewStore.ReadAtPath(data, statePath, out string? schemaError);
        check(invalidSchema is null && schemaError?.Contains("schema version", StringComparison.OrdinalIgnoreCase) == true,
            "reader rejects unsupported schema version");

        File.WriteAllBytes(statePath, new byte[256 * 1024 + 1]);
        var oversized = ShadowReviewStore.ReadAtPath(data, statePath, out string? oversizedError);
        check(oversized is null && oversizedError?.Contains("read limit", StringComparison.Ordinal) == true,
            "reader bounds state input to 256 KiB");

        var tooLarge = summary with
        {
            Models = Enumerable.Range(0, 64).Select(index => new ShadowReviewModelSummary
            {
                Name = index.ToString() + new string('m', 4094),
                Detail = string.Empty,
            }).ToArray(),
        };
        bool writeBounded = false;
        try { ShadowReviewStore.WriteAtomic(statePath, snapshot with { History = [tooLarge] }); }
        catch (InvalidDataException ex) { writeBounded = ex.Message.Contains("write limit", StringComparison.Ordinal); }
        check(writeBounded, "writer refuses a state snapshot above 256 KiB");

        bool unsafeNameRejected = false;
        try
        {
            ShadowReviewStore.WriteAtomic(statePath, snapshot with
            {
                History = [summary with { ReportFileName = "../outside.json" }],
            });
        }
        catch (InvalidDataException) { unsafeNameRejected = true; }
        check(unsafeNameRejected, "writer rejects a report path instead of a basename");
    }

    static void SuccessRetryAndRestartTests(string root, Action<bool, string> check)
    {
        string folder = Path.Combine(root, "success-retry");
        string data = Path.Combine(folder, "telemetry");
        string store = Path.Combine(folder, "review-store");
        Directory.CreateDirectory(data);
        var clock = new TestClock(Utc(2026, 9, 28, 12));
        int processCount = 0;

        ShadowReviewProcessResult WriteSuccess(ShadowReviewProcessRequest request)
        {
            processCount++;
            Directory.CreateDirectory(Path.GetDirectoryName(request.ReportPath)!);
            File.WriteAllText(request.ReportPath, BuildReport(request.InputCutoffUtc,
                warningCount: 80, warningLength: 3500));
            File.WriteAllText(Path.ChangeExtension(request.ReportPath, ".md"), "immutable report");
            return new ShadowReviewProcessResult(0, string.Empty, string.Empty);
        }

        var first = NewScheduler(data, store, clock, (request, _) => Task.FromResult(WriteSuccess(request)));
        first.Start(CancellationToken.None);
        try
        {
            var completed = WaitForSnapshot(store, data, x => x.Status == "COMPLETED", "first completion");
            var item = completed.History[0];
            check(processCount == 1 && item.State == "SUFFICIENT", "first due review runs and records evaluator result");
            check(completed.NextAttemptUtc == completed.LastSuccessUtc + ShadowReviewScheduler.ReviewPeriod,
                "successful report schedules seven-day review, including current cutoff");
            check(item.PowerMinimumW == 100 && item.PowerMaximumW == 200 &&
                  item.CurrentMinimumA == 8 && item.CurrentMaximumA == 22 &&
                  item.GpuTemperatureMinimumC == 45 && item.GpuTemperatureMaximumC == 66 &&
                  item.TemperatureValidMinutes == 60,
                "ranges and GPU temperature aggregate only selected-cohort test partitions");
            check(item.Models.Length == 1 && item.Models[0].FairRows == 12 &&
                  item.Models[0].OutOfEnvelopeRows == 4 && item.Models[0].FairMaeV == .1,
                "model fair metrics and out-of-envelope count are summarized");
            check(item.SourceRows == 20 && item.RejectedRows == 2 && item.InvalidRows == 3 &&
                  item.Warnings.Any(x => x.StartsWith("Input exclusions:", StringComparison.Ordinal)),
                "source exclusions are visible");
            check(item.Warnings.Any(x => x.Contains("newest selected-cohort test day", StringComparison.Ordinal)),
                "stale selected-cohort test day is visible");
            check(File.Exists(Path.Combine(store, "reports", item.ReportFileName)) &&
                  File.Exists(Path.Combine(store, "reports", Path.ChangeExtension(item.ReportFileName, ".md"))),
                "full JSON and Markdown reports remain in the isolated reports directory");
            string fullReport = Path.Combine(store, "reports", item.ReportFileName);
            check(new FileInfo(fullReport).Length > 256 * 1024 &&
                  new FileInfo(Path.Combine(store, "state.json")).Length <= 256 * 1024 &&
                  item.Warnings.Any(x => x.Contains("Additional warning details are in the full report", StringComparison.Ordinal)),
                "large report warnings are compacted with a full-report marker while JSON remains intact");
        }
        finally { first.Dispose(); }

        var stopped = ShadowReviewStore.ReadAtPath(data, Path.Combine(store, "state.json"), out _)!;
        check(stopped.Status == "STOPPED" && stopped.History.Length == 1,
            "normal scheduler stop preserves completed history");

        clock.Advance(ShadowReviewScheduler.ReviewPeriod + TimeSpan.FromMinutes(1));
        var second = NewScheduler(data, store, clock, (request, _) => Task.FromResult(WriteSuccess(request)));
        second.Start(CancellationToken.None);
        try
        {
            var completed = WaitForSnapshot(store, data, x => x.Status == "COMPLETED" && x.History.Length == 2,
                "weekly restart catch-up");
            check(processCount == 2 && completed.History[0].Warnings.Any(x =>
                    x.Contains("without new source or selected-cohort heldout rows", StringComparison.Ordinal)),
                "repeat review distinguishes successful evaluation from absent new evidence");
        }
        finally { second.Dispose(); }

        DateTimeOffset lastSuccess = ShadowReviewStore.ReadAtPath(data, Path.Combine(store, "state.json"), out _)!
            .LastSuccessUtc!.Value;
        clock.Advance(ShadowReviewScheduler.ReviewPeriod + TimeSpan.FromMinutes(1));
        int failedCalls = 0;
        var failing = NewScheduler(data, store, clock, (_, _) =>
        {
            failedCalls++;
            return Task.FromResult(new ShadowReviewProcessResult(12, "", "synthetic child failure"));
        });
        failing.Start(CancellationToken.None);
        ShadowReviewSnapshot failed;
        try
        {
            failed = WaitForSnapshot(store, data, x => x.Status == "FAILED", "failed review");
            check(failedCalls == 1 && failed.History.Length == 2 && failed.LastSuccessUtc == lastSuccess &&
                  failed.NextAttemptUtc == clock.Now + ShadowReviewScheduler.FailureRetryPeriod &&
                  failed.Detail.Contains("synthetic child failure", StringComparison.Ordinal),
                "failure retries after six hours and keeps previous success visible");
        }
        finally { failing.Dispose(); }

        int retryCalls = 0;
        var restart = NewScheduler(data, store, clock, (request, _) =>
        {
            retryCalls++;
            return Task.FromResult(WriteSuccess(request));
        });
        restart.Start(CancellationToken.None);
        try
        {
            Thread.Sleep(80);
            var beforeRetry = ShadowReviewStore.ReadAtPath(data, Path.Combine(store, "state.json"), out _)!;
            check(retryCalls == 0 && beforeRetry.Status == "FAILED" && beforeRetry.Detail == failed.Detail,
                "restart honors the persisted six-hour retry deadline and preserves failure detail");
            clock.Advance(ShadowReviewScheduler.FailureRetryPeriod + TimeSpan.FromSeconds(1));
            var retried = WaitForSnapshot(store, data, x => x.Status == "COMPLETED" && x.History.Length == 3,
                "six-hour retry");
            check(retryCalls == 1 && retried.LastSuccessUtc > lastSuccess,
                "review retries when persisted failure deadline expires");
        }
        finally { restart.Dispose(); }
    }

    static void ReportValidationTests(string root, Action<bool, string> check)
    {
        void RunMalformed(string name, Action<JsonObject> corrupt, string expectedDetail)
        {
            string folder = Path.Combine(root, "report-validation-" + name);
            string data = Path.Combine(folder, "telemetry");
            string store = Path.Combine(folder, "review-store");
            Directory.CreateDirectory(data);
            Directory.CreateDirectory(store);
            var clock = new TestClock(Utc(2026, 9, 28, 12));
            DateTimeOffset previousSuccess = clock.Now - ShadowReviewScheduler.ReviewPeriod - TimeSpan.FromHours(1);
            var prior = new ShadowReviewSnapshot
            {
                DataDirectory = ControlEndpoint.NormalizeDataDirectory(data),
                Status = "COMPLETED",
                Detail = "Previous successful review.",
                UpdatedUtc = previousSuccess,
                StartedUtc = previousSuccess,
                LastSuccessUtc = previousSuccess,
                NextAttemptUtc = clock.Now - TimeSpan.FromSeconds(1),
                History = [CreateSummary(previousSuccess, "prior.json")],
            };
            ShadowReviewStore.WriteAtomic(Path.Combine(store, "state.json"), prior);
            var scheduler = NewScheduler(data, store, clock, (request, _) =>
            {
                Directory.CreateDirectory(Path.GetDirectoryName(request.ReportPath)!);
                JsonObject report = JsonNode.Parse(BuildReport(request.InputCutoffUtc))!.AsObject();
                corrupt(report);
                File.WriteAllText(request.ReportPath, report.ToJsonString());
                return Task.FromResult(new ShadowReviewProcessResult(0, "", ""));
            });
            scheduler.Start(CancellationToken.None);
            try
            {
                var failed = WaitForSnapshot(store, data, x => x.Status == "FAILED", name + " report validation");
                bool retainedPrevious = failed.History.Length == 1 && failed.LastSuccessUtc == previousSuccess;
                bool reportedProblem = failed.Detail.Contains(expectedDetail, StringComparison.OrdinalIgnoreCase);
                check(retainedPrevious && reportedProblem,
                    $"{name} report validation retains prior success and reports its cause " +
                    $"(history={failed.History.Length}, lastSuccess={failed.LastSuccessUtc:O}, " +
                    $"expectedLastSuccess={previousSuccess:O}, detail={failed.Detail})");
                check(failed.NextAttemptUtc == clock.Now + ShadowReviewScheduler.FailureRetryPeriod,
                    name + " report validation failure schedules a six-hour retry");
            }
            finally { scheduler.Dispose(); }
        }

        RunMalformed("missing-metric", report =>
            report["Models"]![0]!["FairMetrics"]!.AsObject().Remove("MeanAbsoluteErrorV"),
            "MeanAbsoluteErrorV");
        RunMalformed("wrong-cutoff", report =>
            report["InputCutoffUtc"] = Utc(2026, 9, 27, 0),
            "cutoff does not match");
    }

    static void AbandonedRunTests(string root, Action<bool, string> check)
    {
        string folder = Path.Combine(root, "abandoned-run");
        string data = Path.Combine(folder, "telemetry");
        string store = Path.Combine(folder, "review-store");
        Directory.CreateDirectory(data);
        Directory.CreateDirectory(store);
        var clock = new TestClock(Utc(2026, 9, 28, 12));
        var running = new ShadowReviewSnapshot
        {
            DataDirectory = ControlEndpoint.NormalizeDataDirectory(data),
            Status = "RUNNING",
            Detail = "Prior process was active.",
            UpdatedUtc = clock.Now - TimeSpan.FromMinutes(20),
            StartedUtc = clock.Now - TimeSpan.FromMinutes(20),
            NextAttemptUtc = clock.Now + ShadowReviewScheduler.FailureRetryPeriod,
            ActiveRunId = "abandoned-run",
        };
        string statePath = Path.Combine(store, "state.json");
        ShadowReviewStore.WriteAtomic(statePath, running);
        int calls = 0;
        var scheduler = NewScheduler(data, store, clock, (request, _) =>
        {
            calls++;
            Directory.CreateDirectory(Path.GetDirectoryName(request.ReportPath)!);
            File.WriteAllText(request.ReportPath, BuildReport(request.InputCutoffUtc));
            return Task.FromResult(new ShadowReviewProcessResult(0, "", ""));
        });
        scheduler.Start(CancellationToken.None);
        try
        {
            var abandoned = WaitForSnapshot(store, data, x => x.Status == "FAILED", "abandoned run recovery");
            check(calls == 0 && abandoned.ActiveRunId is null &&
                  abandoned.NextAttemptUtc == running.NextAttemptUtc,
                "restart converts abandoned RUNNING state and honors its saved retry deadline");
            clock.Advance(ShadowReviewScheduler.FailureRetryPeriod + TimeSpan.FromSeconds(1));
            var completed = WaitForSnapshot(store, data, x => x.Status == "COMPLETED", "abandoned retry");
            check(calls == 1 && completed.History.Length == 1,
                "abandoned review starts after its persisted deadline");
        }
        finally { scheduler.Dispose(); }
    }

    static void SingleWriterAndCancellationTests(string root, Action<bool, string> check)
    {
        string folder = Path.Combine(root, "single-writer");
        string data = Path.Combine(folder, "telemetry");
        string store = Path.Combine(folder, "review-store");
        Directory.CreateDirectory(data);
        var clock = new TestClock(Utc(2026, 9, 28, 12));
        var childStarted = new ManualResetEventSlim();
        int cancelled = 0;
        int secondCalls = 0;
        var first = NewScheduler(data, store, clock, async (_, token) =>
        {
            childStarted.Set();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, token).ConfigureAwait(false); }
            catch (OperationCanceledException) { Interlocked.Increment(ref cancelled); throw; }
            return new ShadowReviewProcessResult(0, "", "");
        });
        first.Start(CancellationToken.None);
        check(childStarted.Wait(TimeSpan.FromSeconds(5)), "blocked fake child starts");

        var second = NewScheduler(data, store, clock, (request, _) =>
        {
            Interlocked.Increment(ref secondCalls);
            Directory.CreateDirectory(Path.GetDirectoryName(request.ReportPath)!);
            File.WriteAllText(request.ReportPath, BuildReport(request.InputCutoffUtc));
            return Task.FromResult(new ShadowReviewProcessResult(0, "", ""));
        });
        second.Start(CancellationToken.None);
        try
        {
            Thread.Sleep(100);
            var during = ShadowReviewStore.ReadAtPath(data, Path.Combine(store, "state.json"), out _)!;
            check(secondCalls == 0 && during.Status == "RUNNING" && during.ActiveRunId is not null,
                "review.lock enforces a single scheduler writer");

            first.Dispose();
            var stopped = ShadowReviewStore.ReadAtPath(data, Path.Combine(store, "state.json"), out _)!;
            check(cancelled == 1 && stopped.Status == "STOPPED" &&
                  stopped.Detail.Contains("shutdown stopped", StringComparison.OrdinalIgnoreCase),
                "shutdown cancels the active child and preserves interrupted status detail");

            clock.Advance(ShadowReviewScheduler.FailureRetryPeriod + TimeSpan.FromSeconds(1));
            var completed = WaitForSnapshot(store, data, x => x.Status == "COMPLETED", "second writer after release");
            check(secondCalls == 1 && completed.History.Length == 1,
                "waiting scheduler acquires the lock after its prior owner exits");
        }
        finally
        {
            first.Dispose();
            second.Dispose();
            childStarted.Dispose();
        }
    }

    static void StoreInsideTelemetryTests(string root, Action<bool, string> check)
    {
        string data = Path.Combine(root, "same-directory-store");
        Directory.CreateDirectory(data);
        int calls = 0;
        using var rejected = new ManualResetEventSlim();
        var clock = new TestClock(Utc(2026, 9, 28, 12));
        var scheduler = new ShadowReviewScheduler(data, data,
            (message, _) =>
            {
                if (message.Contains("resolve the offline shadow review store", StringComparison.Ordinal))
                    rejected.Set();
            },
            () => clock.Now,
            (_, _) =>
        {
            calls++;
            return Task.FromResult(new ShadowReviewProcessResult(0, "", ""));
        }, TimeSpan.FromMilliseconds(15), TimeSpan.FromSeconds(3));
        scheduler.Start(CancellationToken.None);
        try
        {
            check(rejected.Wait(TimeSpan.FromSeconds(5)), "store equal to telemetry is rejected");
        }
        finally { scheduler.Dispose(); }
        check(calls == 0 && !File.Exists(Path.Combine(data, "review.lock")),
            "equal store path is rejected before any write");
    }

    static void ChildProcessIntegrationTest(string root, Action<bool, string> check)
    {
        string folder = Path.Combine(root, "child-integration");
        string data = Path.Combine(folder, "empty-telemetry");
        string store = Path.Combine(folder, "isolated-review-store");
        Directory.CreateDirectory(data);
        var scheduler = new ShadowReviewScheduler(data, store, null,
            static () => DateTimeOffset.UtcNow,
            ShadowReviewScheduler.RunEvaluatorChildForTestAsync,
            TimeSpan.FromMilliseconds(50), TimeSpan.FromSeconds(30));
        scheduler.Start(CancellationToken.None);
        try
        {
            var result = WaitForSnapshot(store, data, x => x.Status == "COMPLETED", "real evaluator child");
            check(result.History.Length == 1 && result.History[0].State == "INSUFFICIENT_DATA" &&
                  result.History[0].SourceRows == 0,
                "actual current apphost or dotnet-plus-assembly child produces a parsed empty-data report");
            check(File.Exists(Path.Combine(store, "reports", result.History[0].ReportFileName)) &&
                  Path.GetDirectoryName(ShadowReviewStore.StatePath(data)) != Path.GetFullPath(data),
                "real child writes only to the isolated review store");
        }
        finally { scheduler.Dispose(); }
    }

    static ShadowReviewScheduler NewScheduler(string data, string store, TestClock clock,
        Func<ShadowReviewProcessRequest, CancellationToken, Task<ShadowReviewProcessResult>> runner) =>
        new(data, store, null, () => clock.Now, runner,
            TimeSpan.FromMilliseconds(15), TimeSpan.FromSeconds(3));

    static ShadowReviewSnapshot WaitForSnapshot(string store, string data,
        Func<ShadowReviewSnapshot, bool> predicate, string description)
    {
        ShadowReviewSnapshot? last = null;
        string? error = null;
        bool arrived = SpinWait.SpinUntil(() =>
        {
            last = ShadowReviewStore.ReadAtPath(data, Path.Combine(store, "state.json"), out error);
            return last is not null && predicate(last);
        }, TimeSpan.FromSeconds(15));
        if (!arrived)
            throw new TimeoutException($"Timed out waiting for {description}. Last status: {last?.Status}; detail: {last?.Detail}; read error: {error}.");
        return last!;
    }

    static string BuildReport(DateTimeOffset cutoff, string state = "SUFFICIENT",
        int warningCount = 0, int warningLength = 0)
    {
        string firstTestDay = cutoff.AddDays(-12).ToString("yyyy-MM-dd");
        string secondTestDay = cutoff.AddDays(-11).ToString("yyyy-MM-dd");
        DateTimeOffset onset = cutoff.AddDays(-12).AddMinutes(30);
        var report = new
        {
            State = state,
            Conclusion = "Synthetic test report.",
            WinnerModel = "power-model",
            InputCutoffUtc = cutoff,
            FrozenCutoffFingerprint = "frozen-fingerprint",
            ModelIdentity = "shadow-regression-v2",
            ConfigurationIdentity = "configuration-identity",
            Coverage = new
            {
                CohortKey = "cohort-a",
                SupportedDays = 8,
                TrainingDays = 3,
                CalibrationDays = 2,
                TestDays = 3,
                TrainingRows = 30,
                CalibrationRows = 20,
                TestRows = 15,
                FairHeldoutRows = 12,
                TrainingDayKeys = new[] { "2026-09-01", "2026-09-02", "2026-09-03" },
                CalibrationDayKeys = new[] { "2026-09-04", "2026-09-05" },
                TestDayKeys = new[] { firstTestDay, secondTestDay },
            },
            DayPartitions = new[]
            {
                new
                {
                    CohortKey = "cohort-a", UtcDate = firstTestDay, IsSupported = true,
                    PowerMinimumW = (double?)100, PowerMaximumW = (double?)150,
                    CurrentMinimumA = (double?)8, CurrentMaximumA = (double?)12,
                    TemperatureMinimumC = (double?)45, TemperatureMaximumC = (double?)50,
                    TemperatureValidMinutes = 30,
                },
                new
                {
                    CohortKey = "cohort-a", UtcDate = secondTestDay, IsSupported = true,
                    PowerMinimumW = (double?)120, PowerMaximumW = (double?)200,
                    CurrentMinimumA = (double?)10, CurrentMaximumA = (double?)22,
                    TemperatureMinimumC = (double?)55, TemperatureMaximumC = (double?)66,
                    TemperatureValidMinutes = 30,
                },
                new
                {
                    CohortKey = "other-cohort", UtcDate = secondTestDay, IsSupported = true,
                    PowerMinimumW = (double?)1, PowerMaximumW = (double?)999,
                    CurrentMinimumA = (double?)1, CurrentMaximumA = (double?)99,
                    TemperatureMinimumC = (double?)1, TemperatureMaximumC = (double?)99,
                    TemperatureValidMinutes = 100,
                },
            },
            Models = new[]
            {
                new
                {
                    Name = "power-model", IsAvailable = true, EligibleForRanking = true,
                    OutOfEnvelopeRows = 4, Detail = "Available.",
                    FairMetrics = new
                    {
                        SampleCount = 12, MeanAbsoluteErrorV = .1, RootMeanSquareErrorV = .15,
                        BiasV = 0, TailAbsoluteErrorV = .2,
                        PerCurrentBand = Array.Empty<object>(),
                    },
                },
            },
            Advisory = new
            {
                IsAvailable = true,
                Model = "power-model",
                CalibrationNoiseScaleV = .01,
                CalibrationResiduals = 20,
                ObservedTestRows = 15,
                ObservedHours = 1.0,
                AdvisoryTransitions = 2,
                TransitionsPerObservedHour = 2.0,
                HardwareFaultProbability = "LOW",
                HardwareFaultProbabilityKnown = true,
                Transitions = Array.Empty<object>(),
                SyntheticFaults = new[]
                {
                    new
                    {
                        Label = "STEP", ScenarioIdentity = "scenario-identity", ScenarioState = "AVAILABLE",
                        OnsetTimestampUtc = (DateTimeOffset?)onset,
                        WindowEndTimestampUtc = (DateTimeOffset?)cutoff.AddDays(-11),
                        DetectionLatencySeconds = (double?)180,
                        AttributableTransitions = 1,
                        Detail = "Synthetic scenario.",
                    },
                },
                Detail = "Advisory summary.",
                RowsBelowPracticalVoltageFloor = 0,
                GapResets = 0,
                CalibrationMedianBiasV = .01,
            },
            InputInventory = new
            {
                RowsRead = 20L,
                EligibleRawRows = 18L,
                InvalidRows = 3L,
                RejectedRows = 2L,
                DuplicateRows = 0L,
                ConflictingRows = 0L,
                PartialRows = 0L,
                Truncated = false,
                Files = Array.Empty<object>(),
            },
            Warnings = Enumerable.Range(0, warningCount)
                .Select(index => new string((char)('a' + index % 26), warningLength) + "-" + index)
                .ToArray(),
        };
        return JsonSerializer.Serialize(report);
    }

    static ShadowReviewSummary CreateSummary(DateTimeOffset completed, string reportName) => new()
    {
        RunId = "run-id",
        CompletedUtc = completed,
        InputCutoffUtc = completed.Date,
        State = "SUFFICIENT",
        Conclusion = "Valid baseline.",
        WinnerModel = "power-model",
        CohortKey = "cohort-a",
        ModelIdentity = "shadow-regression-v2",
        ConfigurationIdentity = "config-id",
        FrozenCutoffFingerprint = "fingerprint",
        SupportedDays = 8,
        TestDays = 3,
        FairHeldoutRows = 12,
        TestRows = 15,
        TrainingDays = ["2026-09-01", "2026-09-02", "2026-09-03"],
        CalibrationDays = ["2026-09-04", "2026-09-05"],
        AdvisoryTransitions = 2,
        AdvisoryTransitionsPerObservedHour = 2,
        PowerMinimumW = 100,
        PowerMaximumW = 200,
        CurrentMinimumA = 8,
        CurrentMaximumA = 22,
        GpuTemperatureMinimumC = 45,
        GpuTemperatureMaximumC = 66,
        TemperatureValidMinutes = 60,
        SourceRows = 20,
        RejectedRows = 2,
        InvalidRows = 3,
        Models = [new ShadowReviewModelSummary
        {
            Name = "power-model", IsAvailable = true, EligibleForRanking = true,
            FairRows = 12, OutOfEnvelopeRows = 4, FairMaeV = .1, FairRmseV = .15, Detail = "Available.",
        }],
        SyntheticFaults = [new ShadowReviewSyntheticSummary
        {
            Label = "STEP", ScenarioIdentity = "scenario-identity", State = "AVAILABLE",
            OnsetUtc = completed - TimeSpan.FromHours(2), WindowEndUtc = completed - TimeSpan.FromHours(1),
            DetectionLatencySeconds = 180, AttributableTransitions = 1, Detail = "Synthetic scenario.",
        }],
        Warnings = [],
        ReportFileName = reportName,
    };

    static DateTimeOffset Utc(int year, int month, int day, int hour) =>
        new(year, month, day, hour, 0, 0, TimeSpan.Zero);

    sealed class TestClock
    {
        long utcTicks;
        public TestClock(DateTimeOffset initial) => utcTicks = initial.UtcTicks;
        public DateTimeOffset Now => new(Interlocked.Read(ref utcTicks), TimeSpan.Zero);
        public void Advance(TimeSpan duration) => Interlocked.Add(ref utcTicks, duration.Ticks);
    }
}
