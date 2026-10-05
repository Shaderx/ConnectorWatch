using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace ConnectorWatch;

internal static class ShadowReviewRecoveryStoreTests
{
    const string ReportBytes = "{\"accepted\":true}";

    internal static void Run(string root, Action<bool, string> check)
    {
        MatchingReceiptRecoversRollbackAndIsIdempotent(root, check);
        OrphanReportIsRetainedButUntrusted(root, check);
        ChangedReportCannotRecover(root, check);
        DistinctAndAmbiguousAttempts(root, check);
        CandidateLimitPreservesSchedule(root, check);
        PublicationRejectsUnacceptedSummary(root, check);
    }

    static void MatchingReceiptRecoversRollbackAndIsIdempotent(string root, Action<bool, string> check)
    {
        string folder = Path.Combine(root, "recovery-matching");
        string data = Path.Combine(folder, "telemetry");
        string storePath = Path.Combine(folder, "review-store");
        CreateDirectories(data, storePath);
        var store = new ShadowReviewRecoveryStore(data, storePath);
        DateTimeOffset started = Utc(2026, 9, 28, 12);
        DateTimeOffset completed = started - TimeSpan.FromHours(2);
        var accepted = MakeSummary(started, completed, Guid.NewGuid().ToString("N"));
        Publish(store, storePath, accepted, started);

        DateTimeOffset previousSuccess = started - TimeSpan.FromDays(7);
        var running = MakeSnapshot(data, "RUNNING", started, previousSuccess,
            started + ShadowReviewScheduler.FailureRetryPeriod, accepted.RunId, accepted.RunId);
        DateTimeOffset checkedAt = started + TimeSpan.FromHours(1);
        var recovered = store.Reconcile(running, checkedAt);

        check(recovered.ProjectionWasReplaced && recovered.Changed && !File.Exists(Path.Combine(storePath, "state.json")),
            "receipt recovery returns a replacement projection without writing state.json");
        check(recovered.Snapshot.Status == "COMPLETED" && recovered.Snapshot.History.Length == 1 &&
              recovered.Snapshot.History[0].RunId == accepted.RunId && recovered.Snapshot.Recovery.RecoveredCount == 1,
            "matching durable receipt restores its accepted summary");
        check(recovered.Snapshot.LastSuccessUtc == completed && recovered.Snapshot.NextAttemptUtc ==
              completed + ShadowReviewScheduler.ReviewPeriod && completed < started,
            "matching run identity restores the recorded weekly deadline after clock rollback");

        ShadowReviewStore.WriteAtomic(Path.Combine(storePath, "state.json"), recovered.Snapshot);
        var repeated = store.Reconcile(recovered.Snapshot, checkedAt);
        check(!repeated.ProjectionWasReplaced && repeated.Snapshot.History.Length == 1 &&
              repeated.Snapshot.Recovery.RecoveredCount == 0 &&
              repeated.Snapshot.NextAttemptUtc == recovered.Snapshot.NextAttemptUtc,
            "a repeated scan does not duplicate history and reports zero newly recovered summaries");
        ShadowReviewStore.WriteAtomic(Path.Combine(storePath, "state.json"), repeated.Snapshot);
        var settled = store.Reconcile(repeated.Snapshot, checkedAt);
        check(!settled.ProjectionWasReplaced && !settled.Changed,
            "a persisted scan with no newly recovered summaries reconciles idempotently");
    }

    static void OrphanReportIsRetainedButUntrusted(string root, Action<bool, string> check)
    {
        string folder = Path.Combine(root, "recovery-orphan");
        string data = Path.Combine(folder, "telemetry");
        string storePath = Path.Combine(folder, "review-store");
        CreateDirectories(data, storePath);
        DateTimeOffset now = Utc(2026, 9, 28, 12);
        var orphan = MakeSummary(now, now, Guid.NewGuid().ToString("N"));
        File.WriteAllText(Path.Combine(storePath, "reports", orphan.ReportFileName), ReportBytes);
        DateTimeOffset priorSuccess = now - TimeSpan.FromDays(1);
        DateTimeOffset retry = now + ShadowReviewScheduler.FailureRetryPeriod;
        var failed = MakeSnapshot(data, "FAILED", now, priorSuccess, retry, null, Guid.NewGuid().ToString("N"));

        var result = new ShadowReviewRecoveryStore(data, storePath).Reconcile(failed, now);
        check(result.Snapshot.History.Length == 0 && result.Snapshot.LastSuccessUtc == priorSuccess &&
              result.Snapshot.NextAttemptUtc == retry && result.Snapshot.Status == "FAILED",
            "an orphan report does not change accepted history or retry scheduling");
        check(result.Snapshot.RetainedReports.Length == 1 &&
              result.Snapshot.RetainedReports[0].ReportFileName == orphan.ReportFileName &&
              result.Snapshot.RetainedReports[0].Detail.Contains("unverified", StringComparison.OrdinalIgnoreCase),
            "an orphan JSON report is retained with an unverified detail");
    }

    static void ChangedReportCannotRecover(string root, Action<bool, string> check)
    {
        string folder = Path.Combine(root, "recovery-corrupt-report");
        string data = Path.Combine(folder, "telemetry");
        string storePath = Path.Combine(folder, "review-store");
        CreateDirectories(data, storePath);
        var recoveryStore = new ShadowReviewRecoveryStore(data, storePath);
        DateTimeOffset started = Utc(2026, 9, 28, 12);
        var accepted = MakeSummary(started, started + TimeSpan.FromMinutes(4), Guid.NewGuid().ToString("N"));
        Publish(recoveryStore, storePath, accepted, started);
        File.WriteAllText(Path.Combine(storePath, "reports", accepted.ReportFileName), "changed after receipt");
        DateTimeOffset retry = started + ShadowReviewScheduler.FailureRetryPeriod;
        var running = MakeSnapshot(data, "RUNNING", started, null, retry, accepted.RunId, accepted.RunId);

        var result = recoveryStore.Reconcile(running, started + TimeSpan.FromMinutes(5));
        check(result.Snapshot.History.Length == 0 && result.Snapshot.Status == "RUNNING" &&
              result.Snapshot.NextAttemptUtc == retry,
            "a report that fails its receipt hash cannot restore history or the schedule");
        check(result.Snapshot.Recovery.Status == "ATTENTION" &&
              result.Snapshot.RetainedReports.Any(item => item.ReportFileName == accepted.ReportFileName),
            "a changed report is reported and remains visible as unverified");
    }

    static void DistinctAndAmbiguousAttempts(string root, Action<bool, string> check)
    {
        string newerFolder = Path.Combine(root, "recovery-newer-attempt");
        string newerData = Path.Combine(newerFolder, "telemetry");
        string newerStorePath = Path.Combine(newerFolder, "review-store");
        CreateDirectories(newerData, newerStorePath);
        var newerStore = new ShadowReviewRecoveryStore(newerData, newerStorePath);
        DateTimeOffset firstStart = Utc(2026, 9, 28, 12);
        var matching = MakeSummary(firstStart, firstStart + TimeSpan.FromMinutes(5), Guid.NewGuid().ToString("N"));
        DateTimeOffset newerStart = firstStart + TimeSpan.FromMinutes(10);
        var newer = MakeSummary(newerStart, firstStart + TimeSpan.FromMinutes(20), Guid.NewGuid().ToString("N"));
        Publish(newerStore, newerStorePath, matching, firstStart);
        Publish(newerStore, newerStorePath, newer, newerStart);
        var running = MakeSnapshot(newerData, "RUNNING", firstStart, firstStart - TimeSpan.FromDays(2),
            firstStart + ShadowReviewScheduler.FailureRetryPeriod, matching.RunId, matching.RunId);

        var advanced = newerStore.Reconcile(running, firstStart + TimeSpan.FromHours(1)).Snapshot;
        check(advanced.Status == "COMPLETED" && advanced.LastAttemptRunId == newer.RunId &&
              advanced.LastSuccessUtc == newer.CompletedUtc && advanced.History.Length == 2,
            "a distinct receipt with proven later start and completion advances the recovered attempt");

        string ambiguousFolder = Path.Combine(root, "recovery-ambiguous-attempt");
        string ambiguousData = Path.Combine(ambiguousFolder, "telemetry");
        string ambiguousStorePath = Path.Combine(ambiguousFolder, "review-store");
        CreateDirectories(ambiguousData, ambiguousStorePath);
        var ambiguousStore = new ShadowReviewRecoveryStore(ambiguousData, ambiguousStorePath);
        var first = MakeSummary(firstStart, firstStart + TimeSpan.FromMinutes(5), Guid.NewGuid().ToString("N"));
        var collision = MakeSummary(firstStart, firstStart + TimeSpan.FromMinutes(30), Guid.NewGuid().ToString("N"));
        Publish(ambiguousStore, ambiguousStorePath, first, firstStart);
        Publish(ambiguousStore, ambiguousStorePath, collision, firstStart);
        DateTimeOffset priorRetry = firstStart + ShadowReviewScheduler.FailureRetryPeriod;
        var uncertain = MakeSnapshot(ambiguousData, "RUNNING", firstStart, firstStart - TimeSpan.FromDays(2),
            priorRetry, first.RunId, first.RunId);

        var preserved = ambiguousStore.Reconcile(uncertain, firstStart + TimeSpan.FromHours(1)).Snapshot;
        check(preserved.Status == "COMPLETED" && preserved.LastSuccessUtc == first.CompletedUtc &&
              preserved.NextAttemptUtc == first.CompletedUtc + ShadowReviewScheduler.ReviewPeriod &&
              preserved.Recovery.Status == "ATTENTION" &&
              preserved.Recovery.Detail.Contains("ambiguous clock ordering", StringComparison.Ordinal),
            "ambiguous distinct receipts preserve the proven matching completion and report attention");
    }

    static void CandidateLimitPreservesSchedule(string root, Action<bool, string> check)
    {
        string folder = Path.Combine(root, "recovery-candidate-limit");
        string data = Path.Combine(folder, "telemetry");
        string storePath = Path.Combine(folder, "review-store");
        CreateDirectories(data, storePath);
        var recoveryStore = new ShadowReviewRecoveryStore(data, storePath);
        DateTimeOffset started = Utc(2026, 9, 28, 12);
        for (int index = 0; index < 129; index++)
        {
            var summary = MakeSummary(started.AddSeconds(index), started.AddSeconds(index + 1), Guid.NewGuid().ToString("N"));
            Publish(recoveryStore, storePath, summary, started.AddSeconds(index));
        }
        DateTimeOffset deadline = started + ShadowReviewScheduler.FailureRetryPeriod;
        var failed = MakeSnapshot(data, "FAILED", started, started - TimeSpan.FromDays(1), deadline,
            null, Guid.NewGuid().ToString("N"));

        var result = recoveryStore.Reconcile(failed, started + TimeSpan.FromMinutes(1)).Snapshot;
        check(result.Recovery.ScanIncomplete && result.Recovery.Status == "ATTENTION" &&
              result.Status == "FAILED" && result.NextAttemptUtc == deadline,
            "a bounded partial receipt scan keeps the existing schedule and marks recovery attention");
    }

    static void PublicationRejectsUnacceptedSummary(string root, Action<bool, string> check)
    {
        string folder = Path.Combine(root, "recovery-reject-publication");
        string data = Path.Combine(folder, "telemetry");
        string storePath = Path.Combine(folder, "review-store");
        CreateDirectories(data, storePath);
        DateTimeOffset started = Utc(2026, 9, 28, 12);
        var summary = MakeSummary(started, started + TimeSpan.FromMinutes(2), Guid.NewGuid().ToString("N")) with
        {
            State = "FAILED",
        };
        File.WriteAllText(Path.Combine(storePath, "reports", summary.ReportFileName), ReportBytes);
        bool rejected = false;
        try
        {
            new ShadowReviewRecoveryStore(data, storePath).PublishCompletion(summary, started,
                Encoding.UTF8.GetBytes(ReportBytes));
        }
        catch (InvalidDataException) { rejected = true; }
        check(rejected && !Directory.Exists(Path.Combine(storePath, "completions")),
            "publication rejects a failed summary before creating a durable completion record");
    }

    static void Publish(ShadowReviewRecoveryStore store, string storePath,
        ShadowReviewSummary summary, DateTimeOffset started)
    {
        File.WriteAllText(Path.Combine(storePath, "reports", summary.ReportFileName), ReportBytes);
        store.PublishCompletion(summary, started, Encoding.UTF8.GetBytes(ReportBytes));
    }

    static void CreateDirectories(string data, string store)
    {
        Directory.CreateDirectory(data);
        Directory.CreateDirectory(Path.Combine(store, "reports"));
    }

    static ShadowReviewSnapshot MakeSnapshot(string data, string status, DateTimeOffset updated,
        DateTimeOffset? lastSuccess, DateTimeOffset nextAttempt, string? activeRunId, string? lastAttemptRunId) => new()
    {
        DataDirectory = ControlEndpoint.NormalizeDataDirectory(data),
        Status = status,
        Detail = "Recovery test snapshot.",
        UpdatedUtc = updated,
        StartedUtc = updated,
        LastSuccessUtc = lastSuccess,
        NextAttemptUtc = nextAttempt,
        ActiveRunId = activeRunId,
        LastAttemptRunId = lastAttemptRunId,
    };

    static ShadowReviewSummary MakeSummary(DateTimeOffset started, DateTimeOffset completed, string runId)
    {
        string stamp = started.ToUniversalTime().UtcDateTime.ToString("yyyyMMdd'T'HHmmssfff'Z'", CultureInfo.InvariantCulture);
        return new ShadowReviewSummary
        {
            RunId = runId,
            CompletedUtc = completed,
            InputCutoffUtc = new DateTimeOffset(started.ToUniversalTime().UtcDateTime.Date, TimeSpan.Zero),
            State = "SUFFICIENT",
            Conclusion = "Recovered test completion.",
            WinnerModel = "power-model",
            CohortKey = "cohort-a",
            ModelIdentity = "shadow-regression-v2",
            ConfigurationIdentity = "configuration-identity",
            FrozenCutoffFingerprint = "frozen-cutoff-fingerprint",
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
            SyntheticFaults = [],
            Warnings = [],
            ReportFileName = stamp + "-" + runId + ".json",
        };
    }

    static DateTimeOffset Utc(int year, int month, int day, int hour) =>
        new(year, month, day, hour, 0, 0, TimeSpan.Zero);
}
