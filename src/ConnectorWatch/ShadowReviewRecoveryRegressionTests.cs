using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ConnectorWatch;

internal static class ShadowReviewRecoveryRegressionTests
{
    static readonly byte[] Report = Encoding.UTF8.GetBytes("{\"accepted\":true}");

    internal static void Run(string root, Action<bool, string> check)
    {
        MatchingRollbackWithAnotherReceipt(root, check);
        DistinctReceiptsPreserveSavedAttempts(root, check);
        InvalidReceiptsDoNotBlockValidRecovery(root, check);
        ConflictingReceiptsPreserveTrustedHistory(root, check);
        OptionalSnapshotCompatibility(root, check);
        BoundedReadsAndScans(root, check);
        LargeCompletionRemainsWritable(root, check);
    }

    static void DistinctReceiptsPreserveSavedAttempts(string root, Action<bool, string> check)
    {
        var fixture = Fixture(root, "review-distinct-attempts");
        var older = Summary(Utc(10), Utc(11));
        Publish(fixture.Store, fixture.Path, older, Utc(10));
        foreach (string status in new[] { "FAILED", "STOPPED", "RUNNING" })
        {
            string runId = Guid.NewGuid().ToString("N");
            var snapshot = Snapshot(fixture.Data, Utc(12)) with
            {
                Status = status, StartedUtc = Utc(12), NextAttemptUtc = Utc(18),
                ActiveRunId = status == "RUNNING" ? runId : null, LastAttemptRunId = runId,
            };
            var recovered = fixture.Store.Reconcile(snapshot, Utc(13)).Snapshot;
            check(recovered.Status == status && recovered.NextAttemptUtc == snapshot.NextAttemptUtc &&
                  recovered.History.Length == 1 && recovered.History[0].RunId == older.RunId,
                status + " distinct newer attempt keeps its deadline when older accepted history recovers");
        }

        var rollback = Summary(Utc(12), Utc(10));
        Publish(fixture.Store, fixture.Path, rollback, Utc(12));
        var saved = Snapshot(fixture.Data, Utc(11)) with
        {
            Status = "COMPLETED", StartedUtc = Utc(10), LastSuccessUtc = older.CompletedUtc,
            LastAttemptRunId = older.RunId, History = [older],
            NextAttemptUtc = older.CompletedUtc + ShadowReviewScheduler.ReviewPeriod,
        };
        var ambiguous = fixture.Store.Reconcile(saved, Utc(13)).Snapshot;
        check(ambiguous.Status == saved.Status && ambiguous.LastSuccessUtc == saved.LastSuccessUtc &&
              ambiguous.NextAttemptUtc == saved.NextAttemptUtc && ambiguous.History.Length == 2 &&
              ambiguous.Recovery.Status == "ATTENTION",
            "a distinct later-starting rollback receipt restores history but preserves the saved deadline with attention");

        var third = Summary(Utc(14), Utc(10).AddMinutes(30));
        Publish(fixture.Store, fixture.Path, third, Utc(14));
        var middle = Summary(Utc(13), Utc(15));
        Publish(fixture.Store, fixture.Path, middle, Utc(13));
        ambiguous = fixture.Store.Reconcile(saved, Utc(16)).Snapshot;
        check(ambiguous.LastSuccessUtc == saved.LastSuccessUtc && ambiguous.NextAttemptUtc == saved.NextAttemptUtc &&
              ambiguous.History.Length == 4 && ambiguous.Recovery.Status == "ATTENTION",
            "a distinct receipt cannot advance the deadline when another recovered attempt has ambiguous later timing");
        File.Delete(ReceiptPath(fixture.Path, older));
        ambiguous = fixture.Store.Reconcile(saved, Utc(16)).Snapshot;
        check(ambiguous.LastSuccessUtc == saved.LastSuccessUtc && ambiguous.NextAttemptUtc == saved.NextAttemptUtc &&
              ambiguous.History.Length == 4 && ambiguous.Recovery.Status == "ATTENTION",
            "legacy accepted history without its own receipt keeps its deadline when distinct receipt ordering is ambiguous");
    }

    static void MatchingRollbackWithAnotherReceipt(string root, Action<bool, string> check)
    {
        var fixture = Fixture(root, "review-matching-rollback");
        DateTimeOffset firstStart = Utc(10);
        var first = Summary(firstStart, Utc(11));
        var second = Summary(Utc(12), Utc(10));
        Publish(fixture.Store, fixture.Path, first, firstStart);
        Publish(fixture.Store, fixture.Path, second, Utc(12));
        foreach (string status in new[] { "RUNNING", "FAILED" })
        {
            var snapshot = Snapshot(fixture.Data, Utc(12)) with
            {
                Status = status, StartedUtc = Utc(12), NextAttemptUtc = Utc(18),
                ActiveRunId = status == "RUNNING" ? second.RunId : null,
                LastAttemptRunId = second.RunId,
            };
            var recovered = fixture.Store.Reconcile(snapshot, Utc(13)).Snapshot;
            check(recovered.Status == "COMPLETED" && recovered.LastAttemptRunId == second.RunId &&
                  recovered.LastSuccessUtc == second.CompletedUtc &&
                  recovered.NextAttemptUtc == second.CompletedUtc + ShadowReviewScheduler.ReviewPeriod &&
                  recovered.History.Length == 2 && recovered.Recovery.Status == "ATTENTION",
                status + " matching receipt finishes its rollback attempt despite an older receipt with a later completion clock");
        }
    }

    static void InvalidReceiptsDoNotBlockValidRecovery(string root, Action<bool, string> check)
    {
        var cases = new (string Name, Action<JsonObject> Corrupt)[]
        {
            ("version", node => node["SchemaVersion"] = 2),
            ("identity", node => node["DataDirectory"] = Path.Combine(root, "other-telemetry")),
            ("null-hash", node => node["ReportSha256"] = null),
            ("hash", node => node["ReportSha256"] = new string('0', 64)),
            ("cutoff", node => node["InputCutoffUtc"] = Utc(0).AddDays(1).ToString("O")),
            ("run", node => node["RunId"] = Guid.NewGuid().ToString("N")),
            ("path", node => node["ReportFileName"] = "../outside.json"),
            ("state", node => node["Summary"]!["State"] = "TRUNCATED_INPUT"),
            ("length", node => node["ReportLength"] = -1),
            ("required-summary", node => node["Summary"]!.AsObject().Remove("Models")),
        };
        foreach (var item in cases)
        {
            var fixture = Fixture(root, "review-invalid-" + item.Name);
            var good = Summary(Utc(10), Utc(11));
            var bad = Summary(Utc(12), Utc(13));
            Publish(fixture.Store, fixture.Path, good, Utc(10));
            Publish(fixture.Store, fixture.Path, bad, Utc(12));
            string receipt = ReceiptPath(fixture.Path, bad);
            var node = JsonNode.Parse(File.ReadAllText(receipt))!.AsObject();
            item.Corrupt(node);
            File.WriteAllText(receipt, node.ToJsonString());
            var recovered = new ShadowReviewRecoveryStore(fixture.Data, fixture.Path)
                .Reconcile(Snapshot(fixture.Data, Utc(14)), Utc(14)).Snapshot;
            check(recovered.History.Length == 1 && recovered.History[0].RunId == good.RunId &&
                  recovered.LastSuccessUtc == good.CompletedUtc && recovered.Recovery.Status == "ATTENTION" &&
                  recovered.RetainedReports.Any(report => report.ReportFileName == bad.ReportFileName),
                "invalid " + item.Name + " receipt is isolated while another valid completion recovers");
        }
    }

    static void ConflictingReceiptsPreserveTrustedHistory(string root, Action<bool, string> check)
    {
        var fixture = Fixture(root, "review-conflicting-run");
        var trusted = Summary(Utc(10), Utc(11));
        var conflict = Summary(Utc(12), Utc(13)) with { RunId = trusted.RunId };
        conflict = conflict with { ReportFileName = ReportName(Utc(12), conflict.RunId) };
        Publish(fixture.Store, fixture.Path, trusted, Utc(10));
        Publish(fixture.Store, fixture.Path, conflict, Utc(12));
        var snapshot = Snapshot(fixture.Data, Utc(14)) with
        {
            Status = "FAILED", StartedUtc = Utc(14), NextAttemptUtc = Utc(20),
            LastSuccessUtc = trusted.CompletedUtc, History = [trusted],
            LastAttemptRunId = Guid.NewGuid().ToString("N"),
        };
        var recovered = fixture.Store.Reconcile(snapshot, Utc(15)).Snapshot;
        check(recovered.History.Length == 1 && recovered.History[0].ReportFileName == trusted.ReportFileName &&
              recovered.Status == "FAILED" && recovered.NextAttemptUtc == snapshot.NextAttemptUtc &&
              recovered.Recovery.Status == "ATTENTION" && recovered.Recovery.RecoveredCount == 0,
            "conflicting receipts for one run preserve the existing trusted summary and newer failure deadline");
    }

    static void OptionalSnapshotCompatibility(string root, Action<bool, string> check)
    {
        var fixture = Fixture(root, "review-snapshot-compatibility");
        string path = Path.Combine(fixture.Path, "state.json");
        ShadowReviewStore.WriteAtomic(path, Snapshot(fixture.Data, Utc(12)));
        var original = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        var legacy = (JsonObject)original.DeepClone();
        legacy.Remove("LastAttemptRunId");
        legacy.Remove("RetainedReports");
        legacy.Remove("Recovery");
        File.WriteAllText(path, legacy.ToJsonString());
        var loaded = ShadowReviewStore.ReadAtPath(fixture.Data, path, out string? error);
        check(loaded is not null && error is null && loaded.LastAttemptRunId is null &&
              loaded.RetainedReports.Length == 0 && loaded.Recovery.Status == "NOT_CHECKED",
            "legacy schema-one snapshots supply defaults for missing optional recovery fields");
        var cases = new (string Name, Action<JsonObject> Corrupt)[]
        {
            ("attempt", node => node["LastAttemptRunId"] = 7),
            ("retained-null", node => node["RetainedReports"] = null),
            ("retained-path", node => node["RetainedReports"] = new JsonArray(new JsonObject
                { ["ReportFileName"] = "../outside.json", ["Detail"] = "unverified" })),
            ("recovery-null", node => node["Recovery"] = null),
            ("recovery-status", node => node["Recovery"]!["Status"] = "UNKNOWN"),
            ("recovery-count", node => node["Recovery"]!["RecoveredCount"] = "one"),
            ("recovery-negative", node => node["Recovery"]!["RecoveredCount"] = -1),
            ("recovery-incomplete", node => node["Recovery"]!["ScanIncomplete"] = "yes"),
        };
        foreach (var item in cases)
        {
            var node = (JsonObject)original.DeepClone();
            item.Corrupt(node);
            File.WriteAllText(path, node.ToJsonString());
            check(ShadowReviewStore.ReadAtPath(fixture.Data, path, out error) is null && !string.IsNullOrWhiteSpace(error),
                "malformed present optional snapshot field is rejected: " + item.Name);
        }
    }

    static void BoundedReadsAndScans(string root, Action<bool, string> check)
    {
        using var growing = new GrowingStream(128);
        bool rejected = false;
        try { ShadowReviewRecoveryStore.ReadStreamBounded(growing, 32); }
        catch (InvalidDataException) { rejected = true; }
        check(rejected && growing.BytesRead == 33,
            "a stream that grows beyond its initial length reads only the limit plus one byte");
        using var exact = new MemoryStream(new byte[32]);
        check(ShadowReviewRecoveryStore.ReadStreamBounded(exact, 32).Length == 32,
            "a report exactly at the content limit remains readable");

        var budget = Fixture(root, "review-content-budget");
        string budgetFile = Path.Combine(budget.Path, "budget.bin");
        File.WriteAllBytes(budgetFile, new byte[32]);
        var reader = typeof(ShadowReviewRecoveryStore).GetMethod("ReadContentBounded", BindingFlags.Static | BindingFlags.NonPublic)!;
        object?[] arguments = [budgetFile, 64, 16L, 0L, false];
        byte[] bytes = (byte[])reader.Invoke(null, arguments)!;
        check(bytes.Length == 0 && (long)arguments[3]! == 0 && (bool)arguments[4]!,
            "the scan content budget rejects a file before reading beyond the remaining bytes");

        foreach (string oversized in new[] { "receipt", "report" })
        {
            var fixture = Fixture(root, "review-oversized-" + oversized);
            var accepted = Summary(Utc(10), Utc(11));
            Publish(fixture.Store, fixture.Path, accepted, Utc(10));
            string path = oversized == "receipt" ? ReceiptPath(fixture.Path, accepted) :
                Path.Combine(fixture.Path, "reports", accepted.ReportFileName);
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Write))
                stream.SetLength((oversized == "receipt" ? 512 * 1024 : 16 * 1024 * 1024) + 1L);
            var recovered = fixture.Store.Reconcile(Snapshot(fixture.Data, Utc(12)), Utc(12)).Snapshot;
            check(recovered.History.Length == 0 && recovered.Recovery.ScanIncomplete &&
                  recovered.Recovery.Status == "ATTENTION",
                "an oversized " + oversized + " cannot recover and marks the scan incomplete");
        }

        var directory = Fixture(root, "review-directory-limit");
        for (int index = 0; index < 4097; index++)
            using (File.Create(Path.Combine(directory.Path, "reports", "ignored-" + index + ".txt"))) { }
        var limited = directory.Store.Reconcile(Snapshot(directory.Data, Utc(12)), Utc(12)).Snapshot;
        check(limited.Recovery.ScanIncomplete && limited.Recovery.Status == "ATTENTION" && limited.RetainedReports.Length == 0,
            "the directory cap counts examined entries even when no entry is a retained JSON report");
    }

    static void LargeCompletionRemainsWritable(string root, Action<bool, string> check)
    {
        var fixture = Fixture(root, "review-near-limit-summary");
        DateTimeOffset started = Utc(10).AddTicks(1234567);
        var summary = Summary(started, Utc(11).AddTicks(7654321));
        int low = 1, high = 4096;
        while (low < high)
        {
            int size = (low + high + 1) / 2;
            try
            {
                ShadowReviewStore.PrepareCompletionSummary(WithLargeModels(summary, size), fixture.Data);
                low = size;
            }
            catch (InvalidDataException) { high = size - 1; }
        }
        summary = WithLargeModels(summary, low);
        Publish(fixture.Store, fixture.Path, summary, started);
        var recovered = fixture.Store.Reconcile(Snapshot(fixture.Data, Utc(12)), Utc(12)).Snapshot;
        var crowded = recovered with
        {
            Status = "FAILED", Detail = new string('\u0001', 2048), UpdatedUtc = Utc(12).AddTicks(1234567),
            StartedUtc = Utc(12).AddTicks(1234567), NextAttemptUtc = Utc(18).AddTicks(1234567),
            LastAttemptRunId = Guid.NewGuid().ToString("N"),
            Recovery = recovered.Recovery with { Status = "ATTENTION", Detail = new string('\u0001', 2048) },
        };
        string path = Path.Combine(fixture.Path, "state.json");
        var saved = ShadowReviewStore.WriteAtomic(path, crowded);
        check(saved.History.Length == 1 && saved.History[0].RunId == summary.RunId &&
              new FileInfo(path).Length <= ShadowReviewStore.MaximumStateBytes,
            "a near-limit committed summary remains writable with escaped diagnostics and precise attempt timestamps");

        var other = summary with { RunId = Guid.NewGuid().ToString("N") };
        other = other with { ReportFileName = ReportName(started, other.RunId) };
        saved = ShadowReviewStore.WriteAtomic(path, crowded with
        {
            History = [summary, other],
            RetainedReports = Enumerable.Range(0, 12).Select(index => new ShadowReviewRetainedReport
            {
                ReportFileName = "retained-" + index + ".json", Detail = new string('\u0001', 512),
            }).ToArray(),
        });
        check(saved.History.Length == 1 && saved.History[0].RunId == summary.RunId &&
              saved.Recovery.Detail.Length <= 128 && saved.RetainedReports.Length == 0 &&
              new FileInfo(path).Length <= ShadowReviewStore.MaximumStateBytes,
            "byte-limit compaction shortens recovery diagnostics and removes retained reports before accepted history");
    }

    static ShadowReviewSummary WithLargeModels(ShadowReviewSummary summary, int nameSize) => summary with
    {
        Models = Enumerable.Range(0, 64).Select(_ => new ShadowReviewModelSummary
        {
            Name = new string('x', nameSize), Detail = "Large model identity fixture.",
        }).ToArray(),
    };

    static (string Data, string Path, ShadowReviewRecoveryStore Store) Fixture(string root, string name)
    {
        string path = Path.Combine(root, name, "review-store");
        string data = Path.Combine(root, name, "telemetry");
        Directory.CreateDirectory(data);
        Directory.CreateDirectory(Path.Combine(path, "reports"));
        return (data, path, new ShadowReviewRecoveryStore(data, path));
    }

    static void Publish(ShadowReviewRecoveryStore store, string path, ShadowReviewSummary summary, DateTimeOffset started)
    {
        File.WriteAllBytes(Path.Combine(path, "reports", summary.ReportFileName), Report);
        store.PublishCompletion(summary, started, Report);
    }

    static string ReceiptPath(string path, ShadowReviewSummary summary) =>
        Path.Combine(path, "completions", Path.GetFileNameWithoutExtension(summary.ReportFileName) + ".completion.json");

    static ShadowReviewSnapshot Snapshot(string data, DateTimeOffset now) => new()
    {
        DataDirectory = data, Status = "WAITING", UpdatedUtc = now, Detail = "Recovery regression fixture.",
    };

    static ShadowReviewSummary Summary(DateTimeOffset started, DateTimeOffset completed)
    {
        string runId = Guid.NewGuid().ToString("N");
        return new ShadowReviewSummary
        {
            RunId = runId, CompletedUtc = completed,
            InputCutoffUtc = new DateTimeOffset(started.UtcDateTime.Date, TimeSpan.Zero),
            State = "SUFFICIENT", Conclusion = "Accepted regression fixture.",
            CohortKey = "cohort", ModelIdentity = "model", ConfigurationIdentity = "configuration",
            FrozenCutoffFingerprint = "fingerprint", ReportFileName = ReportName(started, runId),
        };
    }

    static string ReportName(DateTimeOffset started, string runId) =>
        started.UtcDateTime.ToString("yyyyMMdd'T'HHmmssfff'Z'", CultureInfo.InvariantCulture) + "-" + runId + ".json";

    static DateTimeOffset Utc(int hour) => new(2026, 9, 28, hour, 0, 0, TimeSpan.Zero);

    sealed class GrowingStream(int length) : Stream
    {
        public int BytesRead { get; private set; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => 0;
        public override long Position { get => BytesRead; set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count)
        {
            int read = Math.Min(count, length - BytesRead);
            Array.Clear(buffer, offset, read);
            BytesRead += read;
            return read;
        }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
