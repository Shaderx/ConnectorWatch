using System.Globalization;
using System.Text;

namespace ConnectorWatch;

/// <summary>Offline fixtures for the bounded RailJsonLog tail reader.</summary>
public static class RailJsonTailTests
{
    const int BufferSize = 131072;
    static readonly Encoding Utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    public static void Run()
    {
        var folder = Path.Combine(Path.GetTempPath(), "ConnectorWatch-rail-tail-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            LargeFileUsesTail(folder);
            MidRecordAtWindowStartIsDiscarded(folder);
            UnterminatedTrailingRecordIsIgnored(folder);
            ExactWindowBoundariesAreHandled(folder);
            CrLfAndLfAreHandled(folder);
            EmptyAndNoCompleteRecordsFailExplicitly(folder);
            OversizedRecordWithoutTailFailsBoundedly(folder);
            ConcurrentAppendSharingWorks(folder);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
        Console.WriteLine("PASS: RailJson bounded-tail fixtures.");
    }

    static void LargeFileUsesTail(string folder)
    {
        var oldTimestamp = DateTimeOffset.UtcNow.AddSeconds(-2);
        var newestTimestamp = DateTimeOffset.UtcNow.AddSeconds(-1);
        var head = Record(oldTimestamp, new string('h', BufferSize + 4096));
        var tail = Record(newestTimestamp);
        Write(Path.Combine(folder, "large.jsonl"), head + "\n" + tail + "\n");

        Check(ReadTimestamp(Path.Combine(folder, "large.jsonl")) == newestTimestamp,
            "large file selects the newest tail record");
    }

    static void MidRecordAtWindowStartIsDiscarded(string folder)
    {
        var newestTimestamp = DateTimeOffset.UtcNow.AddSeconds(-1);
        // This is intentionally not a complete JSON object. The reader must
        // discard it as the partial first record in a tail window.
        var partialLeading = "{\"old\":\"" + new string('m', BufferSize + 2048);
        var tail = Record(newestTimestamp);
        var path = Path.Combine(folder, "mid-record.jsonl");
        Write(path, partialLeading + "\n" + tail + "\n");

        Check(ReadTimestamp(path) == newestTimestamp,
            "partial leading record is discarded");
    }

    static void UnterminatedTrailingRecordIsIgnored(string folder)
    {
        var completeTimestamp = DateTimeOffset.UtcNow.AddSeconds(-2);
        var partialTimestamp = DateTimeOffset.UtcNow.AddSeconds(-1);
        var complete = Record(completeTimestamp);
        var partial = Record(partialTimestamp)[..^7];
        var path = Path.Combine(folder, "unterminated.jsonl");
        Write(path, complete + "\r\n" + partial);

        Check(ReadTimestamp(path) == completeTimestamp,
            "unterminated trailing record is ignored");
    }

    static void ExactWindowBoundariesAreHandled(string folder)
    {
        var exactTimestamp = DateTimeOffset.UtcNow.AddSeconds(-1);
        var exactPath = Path.Combine(folder, "exact-window.jsonl");
        var exactLine = RecordOfLength(exactTimestamp, BufferSize - 1);
        Write(exactPath, exactLine + "\n");
        Check(new FileInfo(exactPath).Length == BufferSize,
            "exact window fixture has the requested byte length");
        Check(ReadTimestamp(exactPath) == exactTimestamp,
            "file exactly one tail window is readable");

        var prefixTimestamp = DateTimeOffset.UtcNow.AddSeconds(-3);
        var boundaryTimestamp = DateTimeOffset.UtcNow.AddSeconds(-2);
        var newestTimestamp = DateTimeOffset.UtcNow.AddSeconds(-1);
        var prefix = Record(prefixTimestamp) + "\n";
        var tail = Record(newestTimestamp) + "\n";
        var boundaryLength = BufferSize - 1 - Utf8.GetByteCount(tail);
        var boundary = RecordOfLength(boundaryTimestamp, boundaryLength);
        var boundaryPath = Path.Combine(folder, "exact-boundary.jsonl");
        Write(boundaryPath, prefix + boundary + "\n" + tail);
        Check(new FileInfo(boundaryPath).Length - Utf8.GetByteCount(prefix) == BufferSize,
            "tail window starts at a record boundary");
        Check(ReadTimestamp(boundaryPath) == newestTimestamp,
            "record at an exact tail boundary is retained");
    }

    static void CrLfAndLfAreHandled(string folder)
    {
        var firstTimestamp = DateTimeOffset.UtcNow.AddSeconds(-2);
        var newestTimestamp = DateTimeOffset.UtcNow.AddSeconds(-1);
        var path = Path.Combine(folder, "line-endings.jsonl");
        Write(path, Record(firstTimestamp) + "\r\n" + Record(newestTimestamp) + "\n");

        Check(ReadTimestamp(path) == newestTimestamp,
            "CRLF and LF delimiters are normalized");
    }

    static void EmptyAndNoCompleteRecordsFailExplicitly(string folder)
    {
        var emptyPath = Path.Combine(folder, "empty.jsonl");
        Write(emptyPath, string.Empty);
        ExpectIOException(() => ReadTimestamp(emptyPath), "empty file fails explicitly");

        var partialPath = Path.Combine(folder, "partial-only.jsonl");
        Write(partialPath, "partial record with no newline");
        ExpectIOException(() => ReadTimestamp(partialPath),
            "file with no complete record fails explicitly");
    }

    static void OversizedRecordWithoutTailFailsBoundedly(string folder)
    {
        var oversizedPath = Path.Combine(folder, "oversized.jsonl");
        var oversized = Record(DateTimeOffset.UtcNow.AddSeconds(-1), new string('o', BufferSize + 8192));
        Write(oversizedPath, oversized + "\n");

        // The bounded reader cannot reconstruct a record whose complete line
        // is larger than its window, so it reports no complete record instead
        // of allocating the whole line or returning a partial JSON object.
        ExpectIOException(() => ReadTimestamp(oversizedPath),
            "oversized record without a later tail fails explicitly");
    }

    static void ConcurrentAppendSharingWorks(string folder)
    {
        var completeTimestamp = DateTimeOffset.UtcNow.AddSeconds(-2);
        var appendedTimestamp = DateTimeOffset.UtcNow.AddSeconds(-1);
        var path = Path.Combine(folder, "concurrent.jsonl");
        using var writer = new FileStream(path, FileMode.Create, FileAccess.Write,
            FileShare.ReadWrite | FileShare.Delete);
        WriteBytes(writer, Utf8.GetBytes(Record(completeTimestamp) + "\n"));
        WriteBytes(writer, Utf8.GetBytes(Record(appendedTimestamp)));
        Check(ReadTimestamp(path) == completeTimestamp,
            "reader shares an open appender and ignores its partial tail");
        WriteBytes(writer, Utf8.GetBytes("\n"));
        Check(ReadTimestamp(path) == appendedTimestamp,
            "reader observes the appended record after its delimiter arrives");
    }

    static string Record(DateTimeOffset timestamp, string? padding = null)
    {
        var optionalPadding = padding is null ? string.Empty : ",\"padding\":\"" + padding + "\"";
        return "{\"timestamp_utc\":\"" +
            timestamp.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture) +
            "\",\"input_voltage_v\":12.1,\"power_w\":440.5" + optionalPadding + "}";
    }

    static string RecordOfLength(DateTimeOffset timestamp, int byteLength)
    {
        var emptyPadding = Record(timestamp, string.Empty);
        var paddingLength = byteLength - Utf8.GetByteCount(emptyPadding);
        if (paddingLength < 0) throw new ArgumentOutOfRangeException(nameof(byteLength));
        return Record(timestamp, new string('x', paddingLength));
    }

    static void Write(string path, string content) => File.WriteAllText(path, content, Utf8);

    static void WriteBytes(FileStream stream, byte[] bytes)
    {
        stream.Write(bytes, 0, bytes.Length);
        stream.Flush(flushToDisk: true);
    }

    static DateTimeOffset ReadTimestamp(string path) =>
        new RailJsonLog(new Config { GpuUuid = "fixture", RailJson = path, MaxAgeSeconds = 300 })
            .Read(DateTimeOffset.UtcNow).Timestamp;

    static void ExpectIOException(Action action, string name)
    {
        try
        {
            action();
        }
        catch (IOException)
        {
            return;
        }
        throw new Exception("FAILED: " + name);
    }

    static void Check(bool condition, string name)
    {
        if (!condition) throw new Exception("FAILED: " + name);
    }
}
