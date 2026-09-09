using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;

namespace ConnectorWatch.Gui;

/// <summary>
/// Small filesystem fixtures for the long-horizon reader. The stress case is
/// intentionally separate because it writes a little over one million rows.
/// </summary>
public static class ConfidenceHistoryTests
{
    const string Header = "timestamp_utc,gpu_uuid,input_voltage_v,voltage_timestamp_utc," +
        "analysis_power_w,analysis_power_source,voltage_source,bin_w,reference_v,status," +
        "electrical_source,electrical_freshness_kind,analysis_load_unit,acquisition_health\n";

    public static void Run(Action<bool, string> report)
    {
        if (report is null) throw new ArgumentNullException(nameof(report));
        DailyEvidenceAndRestart(report);
        DuplicateAndBoundaryRowsDoNotInflateEvidence(report);
        ReferenceEpochsRemainDistinctAcrossRestart(report);
        UnknownStaleAndUncharacterizedRows(report);
        AppendedFileMatchesCleanReplay(report);
        RemovedFileKeepsCachedDays(report);
        AgingRetainsTheActiveEpoch(report);
        ReturnedReferenceSurvivesExpiry(report);
        CheckpointWritesAreThrottledAndFlushable(report);
        CompressedHistoryMatchesPlainAndArchiveConversion(report);
    }

    /// <summary>
    /// Optional bounded-memory regression case. It is not part of the normal
    /// GUI self-test because generating a million-row fixture is deliberately
    /// slower than the small correctness fixtures.
    /// </summary>
    public static void RunStress(Action<bool, string> report)
    {
        if (report is null) throw new ArgumentNullException(nameof(report));
        string directory = TemporaryDirectory("stress");
        try
        {
            DateTimeOffset now = new(2036, 1, 20, 12, 0, 0, TimeSpan.Zero);
            const int rowsPerDay = 100_010;
            for (int dayIndex = 1; dayIndex <= 10; dayIndex++)
            {
                DateTimeOffset day = UtcDay(now.AddDays(-dayIndex));
                string path = Path.Combine(directory,
                    "telemetry-" + day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + ".csv");
                using var writer = new StreamWriter(path, false, new UTF8Encoding(false));
                writer.Write(Header);
                for (int i = 0; i < rowsPerDay; i++)
                {
                    var time = day.AddMilliseconds(i * 800);
                    writer.Write(Row(time, 11.9, 12.0, "GPU-A", "connector", "CONNECTOR_POWER", "W", "HEALTHY", "VerifiedSourceTimestamp"));
                }
            }

            var history = new ConfidenceHistory(directory,
                Path.Combine(directory, "confidence-history.json"));
            history.RefreshAsync(now).GetAwaiter().GetResult();
            var newest = history.Days.OrderBy(day => day.Day).LastOrDefault();
            report(newest is not null && newest.ObservationCount >= rowsPerDay - 10,
                "million-row backfill retains newest daily evidence");
            report(File.Exists(Path.Combine(directory, "confidence-history.json")) &&
                new FileInfo(Path.Combine(directory, "confidence-history.json")).Length < 64 * 1024 * 1024,
                "million-row cache stays compact");
        }
        finally
        {
            DeleteTemporaryDirectory(directory);
        }
    }

    static void DailyEvidenceAndRestart(Action<bool, string> report)
    {
        string directory = TemporaryDirectory("daily");
        try
        {
            DateTimeOffset now = new(2036, 2, 20, 12, 0, 0, TimeSpan.Zero);
            DateTimeOffset first = UtcDay(now.AddDays(-2)).AddHours(1);
            DateTimeOffset second = UtcDay(now.AddDays(-1)).AddHours(2);
            WriteRows(directory, first, QualifyingRows(first, 12.0, 11.9));
            WriteRows(directory, second, QualifyingRows(second, 12.0, 11.8));

            string cache = Path.Combine(directory, "confidence-history.json");
            var history = new ConfidenceHistory(directory, cache);
            history.RefreshAsync(now).GetAwaiter().GetResult();
            var records = history.Days.OrderBy(day => day.Day).ToArray();
            report(records.Length == 2, "daily backfill emits one record per completed UTC day");
            report(records.All(day => day.MinuteCount == 10 && day.ObservationCount == 50),
                "daily records count unique supported minute observations across qualifying minutes");
            report(records[0].MedianDropMv is double firstDrop && Math.Abs(firstDrop - 100) < .001 &&
                records[1].MedianDropMv is double secondDrop && Math.Abs(secondDrop - 200) < .001,
                "daily median uses signed recorded reference drop in mV");
            report(records.All(day => day.MinLoad == 425 && day.MaxLoad == 425 &&
                day.MedianLoad == 425 && day.P10Load == 425 && day.P90Load == 425),
                "daily load evidence is retained for W source cohorts");
            report(records.All(day => day.AnchorMedianLoad == 425 &&
                day.AnchorP10Load == 425 && day.AnchorP90Load == 425),
                "qualifying daily records retain the immutable epoch load anchor");
            report(File.Exists(cache) && !File.Exists(cache + ".tmp") &&
                File.ReadAllText(cache).Contains("SchemaVersion", StringComparison.Ordinal),
                "history cache is written atomically with a schema version");

            var replay = new ConfidenceHistory(directory, cache);
            replay.RefreshAsync(now).GetAwaiter().GetResult();
            report(SameDays(records, replay.Days),
                "unchanged restart replays the same immutable daily records");
        }
        finally
        {
            DeleteTemporaryDirectory(directory);
        }
    }

    static void DuplicateAndBoundaryRowsDoNotInflateEvidence(Action<bool, string> report)
    {
        string directory = TemporaryDirectory("duplicates");
        try
        {
            DateTimeOffset now = new(2036, 3, 20, 12, 0, 0, TimeSpan.Zero);
            DateTimeOffset day = UtcDay(now.AddDays(-2));
            var full = Rows(day.AddHours(3), 12.0, 11.9, 5);
            var partial = full.Take(3).Concat(Rows(day.AddHours(3).AddSeconds(5),
                12.0, 11.9, 2)).ToArray();
            WriteRows(directory, day, full);
            WriteRows(directory, day, partial, "-copy");

            // The same sensor timestamp is repeated in both files. A row at
            // UTC midnight is also repeated across adjacent dated files.
            DateTimeOffset boundary = day.AddDays(1);
            var midnight = Rows(boundary, 12.0, 11.9, 5);
            // A recorder can close the prior dated file after midnight. The
            // same boundary rows must still be merged with the next file.
            WriteRows(directory, day, midnight, "-late");
            WriteRows(directory, boundary, midnight);
            WriteRows(directory, boundary, midnight.Take(2), "-copy");

            var history = NewHistory(directory);
            history.RefreshAsync(now).GetAwaiter().GetResult();
            var target = history.Days.FirstOrDefault(record => record.Day == UtcDay(day));
            var next = history.Days.FirstOrDefault(record => record.Day == UtcDay(boundary));
            report(target is not null && target.ObservationCount == 7 && target.MinuteCount == 1,
                "duplicate and partial same-day CSV copies are deduplicated by sensor timestamp");
            report(next is not null && next.ObservationCount == 5,
                "adjacent dated copies do not inflate a UTC-boundary minute");
        }
        finally
        {
            DeleteTemporaryDirectory(directory);
        }
    }

    static void ReferenceEpochsRemainDistinctAcrossRestart(Action<bool, string> report)
    {
        string directory = TemporaryDirectory("epochs");
        try
        {
            DateTimeOffset now = new(2036, 4, 20, 12, 0, 0, TimeSpan.Zero);
            DateTimeOffset day = UtcDay(now.AddDays(-1));
            var rows = Rows(day.AddHours(4), 12.0, 11.9, 5)
                .Concat(Rows(day.AddHours(4).AddMinutes(1), 11.0, 10.9, 5))
                .Concat(Rows(day.AddHours(4).AddMinutes(2), 12.0, 11.9, 5))
                .ToArray();
            WriteRows(directory, day, rows);

            string cache = Path.Combine(directory, "confidence-history.json");
            var history = new ConfidenceHistory(directory, cache);
            history.RefreshAsync(now).GetAwaiter().GetResult();
            var records = history.Days.Where(record => record.Day == UtcDay(day))
                .OrderBy(record => record.FirstSample).ToArray();
            report(records.Length == 3 && records.Select(record => record.Epoch).Distinct().Count() == 3,
                "A to B to A reference transitions create three epochs");
            report(records.All(record => record.ObservationCount == 5 &&
                record.MinuteCount == 1), "each reference epoch keeps its own minute evidence");

            var replay = new ConfidenceHistory(directory, cache);
            replay.RefreshAsync(now).GetAwaiter().GetResult();
            var replayRecords = replay.Days.Where(record => record.Day == UtcDay(day))
                .OrderBy(record => record.FirstSample).ToArray();
            report(records.Select(record => record.Epoch).SequenceEqual(
                replayRecords.Select(record => record.Epoch)),
                "reference epoch IDs survive cache restart");
        }
        finally
        {
            DeleteTemporaryDirectory(directory);
        }
    }

    static void UnknownStaleAndUncharacterizedRows(Action<bool, string> report)
    {
        string directory = TemporaryDirectory("qualification");
        try
        {
            DateTimeOffset now = new(2036, 5, 20, 12, 0, 0, TimeSpan.Zero);
            DateTimeOffset day = UtcDay(now.AddDays(-1));
            var unknown = Rows(day.AddHours(1), 12.0, 11.9, 5,
                gpu: "", electrical: "", loadSource: "", unit: "", health: "HEALTHY");
            var stale = Rows(day.AddHours(2), 12.0, 11.9, 5,
                gpu: "GPU-STALE", electrical: "connector", loadSource: "CONNECTOR_POWER",
                unit: "W", health: "STALE", freshness: "VerifiedSourceTimestamp");
            var uncharacterized = Rows(day.AddHours(3), 12.0, 11.9, 5,
                gpu: "GPU-UNCHARACTERIZED", electrical: "connector",
                loadSource: "CONNECTOR_POWER", unit: "W",
                health: "SENSOR_UNCHARACTERIZED", freshness: "HostPollTimestampUnverified");
            WriteRows(directory, day, unknown.Concat(stale).Concat(uncharacterized));

            var history = NewHistory(directory);
            history.RefreshAsync(now).GetAwaiter().GetResult();
            report(!history.Days.Any(record => record.ObservationCount > 0 &&
                (record.Reason.Contains("UNKNOWN", StringComparison.OrdinalIgnoreCase) ||
                 record.Reason.Contains("STALE", StringComparison.OrdinalIgnoreCase))),
                "unknown and stale sources cannot contribute score evidence");
            var usable = history.Days.FirstOrDefault(record =>
                record.Cohort.Contains("GPU-UNCHARACTERIZED", StringComparison.Ordinal));
            report(usable is not null && usable.ObservationCount == 5 && usable.Unverified,
                "SENSOR_UNCHARACTERIZED remains usable and is marked unverified");
        }
        finally
        {
            DeleteTemporaryDirectory(directory);
        }
    }

    static void AppendedFileMatchesCleanReplay(Action<bool, string> report)
    {
        string incrementalDirectory = TemporaryDirectory("append");
        string cleanDirectory = TemporaryDirectory("clean");
        try
        {
            DateTimeOffset now = new(2036, 6, 20, 12, 0, 0, TimeSpan.Zero);
            DateTimeOffset day = UtcDay(now.AddDays(-1));
            var first = Rows(day.AddHours(5), 12.0, 11.9, 5);
            var second = Rows(day.AddHours(5).AddMinutes(1), 12.0, 11.8, 5);
            string incrementalPath = Path.Combine(incrementalDirectory,
                "telemetry-" + day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + ".csv");
            WriteRows(incrementalDirectory, day, first);
            var incremental = NewHistory(incrementalDirectory);
            incremental.RefreshAsync(now).GetAwaiter().GetResult();
            File.AppendAllText(incrementalPath,
                string.Concat(second.Select(row => Row(row))), new UTF8Encoding(false));
            incremental.RefreshAsync(now).GetAwaiter().GetResult();

            WriteRows(cleanDirectory, day, first.Concat(second));
            var clean = NewHistory(cleanDirectory);
            clean.RefreshAsync(now).GetAwaiter().GetResult();
            bool same = SameDays(incremental.Days, clean.Days);
            string details = same ? "" :
                " incrementalStatus=" + incremental.Status +
                " incrementalDays=" + DescribeDays(incremental.Days) +
                " cleanStatus=" + clean.Status +
                " cleanDays=" + DescribeDays(clean.Days);
            report(same, "appended file invalidation matches a clean replay" + details);
        }
        finally
        {
            DeleteTemporaryDirectory(incrementalDirectory);
            DeleteTemporaryDirectory(cleanDirectory);
        }
    }

    static void AgingRetainsTheActiveEpoch(Action<bool, string> report)
    {
        string directory = TemporaryDirectory("aging");
        try
        {
            DateTimeOffset now = new(2036, 7, 20, 12, 0, 0, TimeSpan.Zero);
            DateTimeOffset oldDay = UtcDay(now.AddDays(-ConfidenceHistory.HistoryDays));
            DateTimeOffset recentDay = UtcDay(now.AddDays(-1));
            WriteRows(directory, oldDay, QualifyingRows(oldDay.AddHours(1), 12.0, 11.9));
            WriteRows(directory, recentDay, QualifyingRows(recentDay.AddHours(1), 12.0, 11.9));
            string recentPath = Path.Combine(directory, $"telemetry-{recentDay:yyyy-MM-dd}.csv");
            File.WriteAllText(recentPath, File.ReadAllText(recentPath).Replace(",425,CONNECTOR_POWER", ",430,CONNECTOR_POWER"));
            string cache = Path.Combine(directory, "confidence-history.json");
            var history = new ConfidenceHistory(directory, cache);
            history.RefreshAsync(now).GetAwaiter().GetResult();
            string? oldEpoch = history.Days.FirstOrDefault(record =>
                record.Day == UtcDay(oldDay))?.Epoch;
            string? recentEpoch = history.Days.FirstOrDefault(record =>
                record.Day == UtcDay(recentDay))?.Epoch;

            history.RefreshAsync(now.AddDays(1)).GetAwaiter().GetResult();
            var retained = history.Days.FirstOrDefault(record =>
                record.Day == UtcDay(recentDay));
            report(oldEpoch is not null && recentEpoch == oldEpoch && retained?.Epoch == recentEpoch,
                "aging out the first retained day preserves the active epoch identity");
            var restarted = NewHistory(directory);
            restarted.RefreshAsync(now.AddDays(1)).GetAwaiter().GetResult();
            var afterRestart = restarted.Days.Single(day => day.Day == recentDay);
            report(retained?.AnchorMedianLoad == 425 && afterRestart.AnchorMedianLoad == 425 && afterRestart.MedianLoad == 430,
                "fixed load anchor survives expiry and restart instead of retraining on recent load");
        }
        finally
        {
            DeleteTemporaryDirectory(directory);
        }
    }

    static void ReturnedReferenceSurvivesExpiry(Action<bool, string> report)
    {
        string directory = TemporaryDirectory("returned-reference");
        try
        {
            DateTimeOffset now = new(2036, 9, 20, 12, 0, 0, TimeSpan.Zero);
            var dates = new[] { UtcDay(now.AddDays(-97)), UtcDay(now.AddDays(-2)), UtcDay(now.AddDays(-1)) };
            for (int i = 0; i < dates.Length; i++)
                WriteRows(directory, dates[i], QualifyingRows(dates[i].AddHours(1), i == 1 ? 12.1 : 12, 11.9));
            var history = NewHistory(directory);
            history.RefreshAsync(now).GetAwaiter().GetResult();
            var original = history.Days.OrderBy(day => day.Day).ToArray();
            var restarted = NewHistory(directory);
            restarted.RefreshAsync(now.AddDays(1)).GetAwaiter().GetResult();
            report(original.Select(day => day.Epoch).Distinct().Count() == 3 &&
                restarted.Days.Single(day => day.Day == dates[2]).Epoch == original[2].Epoch &&
                restarted.Days.Single(day => day.Day == dates[1]).Epoch == original[1].Epoch,
                "A to B to A keeps distinct epochs after the first A expires and the GUI restarts");
        }
        finally { DeleteTemporaryDirectory(directory); }
    }

    static void RemovedFileKeepsCachedDays(Action<bool, string> report)
    {
        string directory = TemporaryDirectory("removed");
        try
        {
            DateTimeOffset now = new(2036, 8, 20, 12, 0, 0, TimeSpan.Zero);
            DateTimeOffset day = UtcDay(now.AddDays(-1));
            string path = Path.Combine(directory, "telemetry-" +
                day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + ".csv");
            WriteRows(directory, day, Rows(day.AddHours(6), 12.0, 11.9, 5));
            var history = NewHistory(directory);
            history.RefreshAsync(now).GetAwaiter().GetResult();
            var before = history.Days.ToArray();

            File.WriteAllText(path, Header);
            history.RefreshAsync(now).GetAwaiter().GetResult();
            report(SameDays(before, history.Days) && history.Status.Contains("truncated", StringComparison.Ordinal),
                "a truncated contributor retains its authoritative completed summary with a warning");
            File.Delete(path);
            history.RefreshAsync(now).GetAwaiter().GetResult();
            report(before.Length > 0 && SameDays(before, history.Days),
                "removing a source CSV retains the authoritative completed day cache");
            WriteRows(directory, day.AddDays(1), Rows(day.AddHours(6).AddMinutes(1), 12, 11.5, 5));
            history.RefreshAsync(now).GetAwaiter().GetResult();
            report(SameDays(before, history.Days),
                "an adjacent partial copy cannot replace the saved day after an original contributor disappears");
        }
        finally
        {
            DeleteTemporaryDirectory(directory);
        }
    }

    static void CheckpointWritesAreThrottledAndFlushable(Action<bool, string> report)
    {
        string directory = TemporaryDirectory("checkpoint");
        try
        {
            DateTimeOffset now = new(2036, 10, 20, 12, 0, 0, TimeSpan.Zero);
            DateTimeOffset day = UtcDay(now.AddDays(-1));
            string path = Path.Combine(directory,
                $"telemetry-{day:yyyy-MM-dd}.csv");
            WriteRows(directory, day, QualifyingRows(day.AddHours(1), 12.0, 11.9));
            string cache = Path.Combine(directory, "confidence-history.json");
            var history = new ConfidenceHistory(directory, cache);
            history.RefreshAsync(now).GetAwaiter().GetResult();
            string initial = File.ReadAllText(cache);
            DateTime initialWrite = File.GetLastWriteTimeUtc(cache);

            history.RefreshAsync(now.AddMinutes(1)).GetAwaiter().GetResult();
            report(initial == File.ReadAllText(cache) &&
                initialWrite == File.GetLastWriteTimeUtc(cache),
                "unchanged refresh skips the history cache write");

            var added = Rows(day.AddHours(2), 12.0, 11.7, 5);
            File.AppendAllText(path, string.Concat(added.Select(Row)),
                new UTF8Encoding(false));
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddSeconds(5));
            history.RefreshAsync(now.AddMinutes(2)).GetAwaiter().GetResult();
            var held = history.Days.Single(record => record.Day == day);
            report(held.ObservationCount == 55 && initial == File.ReadAllText(cache),
                "changed evidence is visible in RAM before the 30-minute checkpoint");

            history.RefreshAsync(now.AddMinutes(31)).GetAwaiter().GetResult();
            string interval = File.ReadAllText(cache);
            report(interval != initial && interval.Contains("\"ObservationCount\":55",
                    StringComparison.Ordinal),
                "the elapsed checkpoint interval persists changed evidence");

            var later = Rows(day.AddHours(3), 12.0, 11.6, 5);
            File.AppendAllText(path, string.Concat(later.Select(Row)),
                new UTF8Encoding(false));
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddSeconds(10));
            history.RefreshAsync(now.AddMinutes(32)).GetAwaiter().GetResult();
            string heldAgain = File.ReadAllText(cache);
            history.FlushAsync().GetAwaiter().GetResult();
            report(heldAgain == interval && File.ReadAllText(cache)
                    .Contains("\"ObservationCount\":60", StringComparison.Ordinal),
                "shutdown flush persists deferred history immediately");
        }
        finally
        {
            DeleteTemporaryDirectory(directory);
        }
    }

    static void CompressedHistoryMatchesPlainAndArchiveConversion(Action<bool, string> report)
    {
        string plainDirectory = TemporaryDirectory("plain-history");
        string compressedDirectory = TemporaryDirectory("compressed-history");
        string warmDirectory = TemporaryDirectory("warm-compressed-history");
        try
        {
            DateTimeOffset now = new(2036, 11, 20, 12, 0, 0, TimeSpan.Zero);
            DateTimeOffset firstDay = UtcDay(now.AddDays(-2));
            DateTimeOffset secondDay = UtcDay(now.AddDays(-1));
            var firstRows = QualifyingRows(firstDay.AddHours(1), 12.0, 11.9);
            var secondRows = QualifyingRows(secondDay.AddHours(1), 12.0, 11.9);
            WriteRows(plainDirectory, firstDay, firstRows);
            WriteRows(plainDirectory, secondDay, secondRows);
            WriteRows(compressedDirectory, firstDay, firstRows);
            WriteRows(compressedDirectory, secondDay, secondRows);
            ConvertTelemetryToGzip(compressedDirectory, firstDay);
            ConvertTelemetryToGzip(compressedDirectory, secondDay);
            // A crash after archive publication can leave both representations.
            WriteRows(compressedDirectory, firstDay, firstRows);

            var plain = NewHistory(plainDirectory);
            plain.RefreshAsync(now).GetAwaiter().GetResult();
            var compressed = NewHistory(compressedDirectory);
            compressed.RefreshAsync(now).GetAwaiter().GetResult();
            report(SameDays(plain.Days, compressed.Days),
                "cold compressed history and duplicate CSV copies match original plain history");

            WriteRows(warmDirectory, firstDay, firstRows);
            WriteRows(warmDirectory, secondDay, secondRows);
            var warm = NewHistory(warmDirectory);
            warm.RefreshAsync(now).GetAwaiter().GetResult();
            var beforeArchive = warm.Days.OrderBy(day => day.Day).ToArray();
            ConvertTelemetryToGzip(warmDirectory, firstDay);
            ConvertTelemetryToGzip(warmDirectory, secondDay);
            warm.RefreshAsync(now.AddMinutes(1)).GetAwaiter().GetResult();
            var afterArchive = warm.Days.OrderBy(day => day.Day).ToArray();
            report(SameDays(beforeArchive, afterArchive) &&
                beforeArchive.Select(day => day.Epoch).SequenceEqual(
                    afterArchive.Select(day => day.Epoch)) &&
                beforeArchive.All(day => day.AnchorMedianLoad == 425 &&
                    day.AnchorP10Load == 425 && day.AnchorP90Load == 425),
                "plain-to-gzip archive conversion preserves epochs and anchors");

            WriteGzipRows(warmDirectory, secondDay,
                secondRows.Concat(Rows(secondDay.AddHours(2), 12.0, 11.7, 5)));
            warm.RefreshAsync(now.AddMinutes(31)).GetAwaiter().GetResult();
            var afterUpdate = warm.Days.Single(day => day.Day == secondDay);
            report(afterUpdate.ObservationCount == 55 &&
                afterUpdate.Epoch == beforeArchive.Single(day => day.Day == secondDay).Epoch &&
                afterUpdate.AnchorMedianLoad == 425 &&
                afterUpdate.AnchorP10Load == 425 && afterUpdate.AnchorP90Load == 425,
                "compressed history accepts later updates without retraining anchors");
        }
        finally
        {
            DeleteTemporaryDirectory(plainDirectory);
            DeleteTemporaryDirectory(compressedDirectory);
            DeleteTemporaryDirectory(warmDirectory);
        }
    }

    static void ConvertTelemetryToGzip(string directory, DateTimeOffset day)
    {
        string plain = Path.Combine(directory,
            $"telemetry-{day:yyyy-MM-dd}.csv");
        string compressed = plain + ".gz";
        using (var input = File.OpenRead(plain))
        using (var output = File.Create(compressed))
        using (var gzip = new GZipStream(output, CompressionLevel.Optimal))
            input.CopyTo(gzip);
        File.Delete(plain);
    }

    static void WriteGzipRows(string directory, DateTimeOffset sensorTime,
        IEnumerable<RowSpec> rows, string suffix = "")
    {
        string path = Path.Combine(directory, "telemetry-" +
            sensorTime.ToUniversalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) +
            suffix + ".csv.gz");
        using var output = File.Create(path);
        using var gzip = new GZipStream(output, CompressionLevel.Optimal);
        using var writer = new StreamWriter(gzip, new UTF8Encoding(false),
            16 * 1024, leaveOpen: false);
        writer.Write(Header);
        foreach (var row in rows) writer.Write(Row(row));
    }

    static ConfidenceHistory NewHistory(string directory) =>
        new(directory, Path.Combine(directory, "confidence-history.json"));

    static DateTimeOffset UtcDay(DateTimeOffset value) =>
        new(value.UtcDateTime.Date, TimeSpan.Zero);

    static bool SameDays(IEnumerable<ConfidenceDay> left,
        IEnumerable<ConfidenceDay> right)
    {
        static string Key(ConfidenceDay day) => string.Join("|",
            day.Day.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
            day.Cohort, day.Epoch, day.MedianDropMv?.ToString("R", CultureInfo.InvariantCulture),
            day.MinuteCount, day.ObservationCount,
            day.FirstSample?.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
            day.LastSample?.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
            day.MinLoad?.ToString("R", CultureInfo.InvariantCulture),
            day.MaxLoad?.ToString("R", CultureInfo.InvariantCulture),
            day.MedianLoad?.ToString("R", CultureInfo.InvariantCulture),
            day.P10Load?.ToString("R", CultureInfo.InvariantCulture),
            day.P90Load?.ToString("R", CultureInfo.InvariantCulture),
            day.AnchorMedianLoad, day.AnchorP10Load, day.AnchorP90Load,
            day.Unverified, day.Reason);
        return left.Select(Key).OrderBy(value => value, StringComparer.Ordinal)
            .SequenceEqual(right.Select(Key).OrderBy(value => value, StringComparer.Ordinal));
    }

    static string DescribeDays(IEnumerable<ConfidenceDay> source) =>
        string.Join(" || ", source.OrderBy(day => day.Day)
            .ThenBy(day => day.Cohort, StringComparer.Ordinal)
            .ThenBy(day => day.Epoch, StringComparer.Ordinal)
            .Select(day => string.Join(",",
                day.Day.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
                day.Cohort, day.Epoch,
                day.MedianDropMv?.ToString("R", CultureInfo.InvariantCulture) ?? "null",
                day.MinuteCount.ToString(CultureInfo.InvariantCulture),
                day.ObservationCount.ToString(CultureInfo.InvariantCulture),
                day.FirstSample?.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture) ?? "null",
                day.LastSample?.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture) ?? "null",
                day.Reason, day.Unverified)));

    static RowSpec[] Rows(DateTimeOffset start, double reference,
        double voltage, int count, string gpu = "GPU-A", string electrical = "connector",
        string loadSource = "CONNECTOR_POWER", string unit = "W", string health = "HEALTHY",
        string freshness = "VerifiedSourceTimestamp") =>
        Enumerable.Range(0, count).Select(index => start.AddSeconds(index))
            .Select(time => new RowSpec(time, voltage, reference, gpu, electrical,
                loadSource, unit, health, freshness)).ToArray();

    static RowSpec[] QualifyingRows(DateTimeOffset start, double reference,
        double voltage) => Enumerable.Range(0, 10)
        .SelectMany(index => Rows(start.AddMinutes(index * 4), reference, voltage, 5))
        .ToArray();

    static string Row(RowSpec row) =>
        Row(row.Time, row.Voltage, row.Reference, row.Gpu, row.Electrical,
            row.LoadSource, row.Unit, row.Health, row.Freshness);

    static string Row(DateTimeOffset time, double voltage, double reference,
        string gpu, string electrical, string loadSource, string unit,
        string health, string freshness) =>
        string.Join(',', time.ToString("O", CultureInfo.InvariantCulture), gpu,
            voltage.ToString("R", CultureInfo.InvariantCulture),
            time.ToString("O", CultureInfo.InvariantCulture), "425", loadSource,
            electrical, "425", reference.ToString("R", CultureInfo.InvariantCulture),
            "NO_SHIFT_DETECTED", electrical, freshness, unit, health) + "\n";

    static void WriteRows(string directory, DateTimeOffset sensorTime,
        IEnumerable<RowSpec> rows, string suffix = "")
    {
        string path = Path.Combine(directory, "telemetry-" +
            sensorTime.ToUniversalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) +
            suffix + ".csv");
        File.WriteAllText(path, Header + string.Concat(rows.Select(Row)),
            new UTF8Encoding(false));
    }

    static string TemporaryDirectory(string name)
    {
        string path = Path.Combine(Path.GetTempPath(),
            "ConnectorWatch-confidence-" + name + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    static void DeleteTemporaryDirectory(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    readonly record struct RowSpec(DateTimeOffset Time, double Voltage,
        double Reference, string Gpu, string Electrical, string LoadSource,
        string Unit, string Health, string Freshness);
}
