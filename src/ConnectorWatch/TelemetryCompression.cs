using System.IO.Compression;
using System.Globalization;
using System.Security;
using System.Security.Cryptography;

namespace ConnectorWatch;

/// <summary>
/// Compresses closed, old daily telemetry files without putting work on the
/// sampling loop.  The current UTC day and the preceding two days remain
/// readable as CSV for the live dashboard.
/// </summary>
public sealed class TelemetryCompressionMaintenance : IDisposable
{
    public const int UncompressedRetentionDays = 3;
    public static readonly TimeSpan MaintenancePeriod = TimeSpan.FromDays(1);

    readonly string directory;
    readonly Action<Exception>? onFailure;
    readonly object lifetimeGate = new();
    CancellationTokenSource? lifetime;
    Task? worker;

    public TelemetryCompressionMaintenance(string directory, Action<Exception>? onFailure = null)
    {
        if (string.IsNullOrWhiteSpace(directory)) throw new ArgumentException("A data directory is required.", nameof(directory));
        this.directory = Path.GetFullPath(directory);
        this.onFailure = onFailure;
    }

    /// <summary>Starts exactly one background maintenance task.</summary>
    public void Start(CancellationToken shutdown)
    {
        lock (lifetimeGate)
        {
            if (worker is not null) throw new InvalidOperationException("Telemetry compression maintenance is already running.");
            lifetime = CancellationTokenSource.CreateLinkedTokenSource(shutdown);
            worker = Task.Run(() => RunLoop(lifetime.Token));
        }
    }

    /// <summary>
    /// Runs one bounded, sequential scan.  It is public for the offline core
    /// self-test; the daemon invokes it from the background worker.
    /// </summary>
    public TelemetryCompressionReport RunOnce(DateTimeOffset utcNow,
        CancellationToken cancellationToken = default)
    {
        var cutoff = DateOnly.FromDateTime(utcNow.UtcDateTime.Date).AddDays(-UncompressedRetentionDays + 1);
        int candidates = 0, compressed = 0, conflicts = 0, failures = 0;
        try
        {
            foreach (var sourcePath in Directory.EnumerateFiles(directory, "telemetry-*.csv",
                         SearchOption.TopDirectoryOnly))
            {
                if (cancellationToken.IsCancellationRequested) break;
                if (!TryGetTelemetryDate(sourcePath, out var date) || date >= cutoff) continue;
                candidates++;
                switch (CompressOne(sourcePath, cancellationToken))
                {
                    case TelemetryCompressionResult.Compressed: compressed++; break;
                    case TelemetryCompressionResult.Conflict: conflicts++; break;
                    case TelemetryCompressionResult.Failed: failures++; break;
                }
            }
        }
        catch (Exception ex) when (IsRecoverable(ex))
        {
            failures++;
            ReportFailure(directory, ex);
        }
        return new(candidates, compressed, conflicts, failures);
    }

    async Task RunLoop(CancellationToken shutdown)
    {
        while (!shutdown.IsCancellationRequested)
        {
            try { _ = RunOnce(DateTimeOffset.UtcNow, shutdown); }
            catch (OperationCanceledException) when (shutdown.IsCancellationRequested) { break; }
            catch (Exception ex) when (IsRecoverable(ex)) { ReportFailure(directory, ex); }
            try { await Task.Delay(MaintenancePeriod, shutdown).ConfigureAwait(false); }
            catch (OperationCanceledException) when (shutdown.IsCancellationRequested) { break; }
        }
    }

    TelemetryCompressionResult CompressOne(string sourcePath, CancellationToken cancellationToken)
    {
        string archivePath = sourcePath + ".gz";
        if (Directory.Exists(archivePath))
            return TelemetryCompressionResult.Conflict;
        if (File.Exists(archivePath))
            return ResumeExistingArchive(sourcePath, archivePath, cancellationToken);

        string tempArchive = sourcePath + ".compression-" + Guid.NewGuid().ToString("N") + ".tmp";
        byte[] expectedHash;
        long expectedLength;
        try
        {
            (expectedHash, expectedLength, var sourceWriteTimeUtc) =
                WriteAndVerifyArchive(sourcePath, tempArchive, cancellationToken);

            // Publish the verified archive before claiming the source.  An
            // abrupt stop can therefore leave a duplicate, valid copy, but
            // never leave the only copy under an internal staging name.
            cancellationToken.ThrowIfCancellationRequested();
            if (File.Exists(archivePath) || Directory.Exists(archivePath))
                return TelemetryCompressionResult.Conflict;
            File.Move(tempArchive, archivePath, false);
            File.SetLastWriteTimeUtc(archivePath, sourceWriteTimeUtc);
            cancellationToken.ThrowIfCancellationRequested();
            return FinishArchive(sourcePath, expectedHash, expectedLength, cancellationToken);
        }
        catch (Exception ex) when (IsRecoverable(ex))
        {
            ReportFailure(sourcePath, ex);
            return TelemetryCompressionResult.Failed;
        }
        finally
        {
            TryDeleteTemporary(tempArchive);
        }
    }

    TelemetryCompressionResult ResumeExistingArchive(string sourcePath, string archivePath,
        CancellationToken cancellationToken)
    {
        try
        {
            var fingerprint = FingerprintFile(sourcePath, cancellationToken);
            if (!MatchesCompressedArchive(archivePath, fingerprint.Hash, fingerprint.Length,
                    cancellationToken))
                return TelemetryCompressionResult.Conflict;
            return FinishArchive(sourcePath, fingerprint.Hash, fingerprint.Length,
                cancellationToken);
        }
        catch (Exception ex) when (ex is InvalidDataException or EndOfStreamException)
        {
            // An invalid or different existing archive is a conflict.  Keep
            // both paths untouched so an operator can recover or inspect them.
            return TelemetryCompressionResult.Conflict;
        }
        catch (Exception ex) when (IsRecoverable(ex))
        {
            ReportFailure(sourcePath, ex);
            return TelemetryCompressionResult.Failed;
        }
    }

    TelemetryCompressionResult FinishArchive(string sourcePath, byte[] expectedHash,
        long expectedLength, CancellationToken cancellationToken)
    {
        string claimedSource = sourcePath + ".compression-" + Guid.NewGuid().ToString("N") + ".source";
        bool sourceClaimed = false;
        try
        {
            // Claim the source by a same-directory atomic move.  This makes
            // the final delete apply to exactly the file that was verified;
            // a replacement at the original path cannot be removed by us.
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(sourcePath, claimedSource, false);
            sourceClaimed = true;
            if (!MatchesHashAndLength(claimedSource, expectedHash, expectedLength, cancellationToken))
                throw new IOException("Telemetry source changed while it was being compressed.");

            try
            {
                File.Delete(claimedSource);
                sourceClaimed = false;
                return TelemetryCompressionResult.Compressed;
            }
            catch (Exception ex) when (IsRecoverable(ex))
            {
                // Keep the original recoverable if the final removal is
                // blocked.  The archive remains valid and is never replaced.
                sourceClaimed = !RestoreClaim(sourcePath, claimedSource);
                throw new IOException("Compressed telemetry archive was kept, but the original could not be removed.", ex);
            }
        }
        catch (Exception ex) when (IsRecoverable(ex))
        {
            if (sourceClaimed) sourceClaimed = !RestoreClaim(sourcePath, claimedSource);
            ReportFailure(sourcePath, ex);
            return TelemetryCompressionResult.Failed;
        }
        finally
        {
            if (sourceClaimed) RestoreClaim(sourcePath, claimedSource);
        }
    }

    static (byte[] Hash, long Length, DateTime LastWriteTimeUtc) WriteAndVerifyArchive(
        string sourcePath, string tempArchive, CancellationToken cancellationToken)
    {
        byte[] expectedHash;
        long expectedLength;
        using (var source = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read,
                   64 * 1024, FileOptions.SequentialScan))
        {
            expectedLength = source.Length;
            DateTime sourceWriteTimeUtc = File.GetLastWriteTimeUtc(sourcePath);
            using (var output = new FileStream(tempArchive, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                       64 * 1024, FileOptions.SequentialScan))
            {
                using (var gzip = new GZipStream(output, CompressionLevel.Fastest, leaveOpen: true))
                using (var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
                {
                    CopyExactly(source, gzip, hash, expectedLength, cancellationToken);
                    expectedHash = hash.GetHashAndReset();
                }
                output.Flush(flushToDisk: true);
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (source.Length != expectedLength)
                throw new IOException("Telemetry source changed while it was being compressed.");
            source.Position = 0;
            if (!MatchesHashAndLength(source, expectedHash, expectedLength, cancellationToken))
                throw new IOException("Telemetry source changed while it was being compressed.");
            if (File.GetLastWriteTimeUtc(sourcePath) != sourceWriteTimeUtc)
                throw new IOException("Telemetry source changed while it was being compressed.");

            if (!MatchesCompressedArchive(tempArchive, expectedHash, expectedLength, cancellationToken))
                throw new InvalidDataException("Telemetry gzip verification did not match the source.");
            return (expectedHash, expectedLength, sourceWriteTimeUtc);
        }
    }

    static void CopyExactly(Stream source, Stream destination, IncrementalHash hash, long length,
        CancellationToken cancellationToken)
    {
        byte[] buffer = new byte[64 * 1024];
        long remaining = length;
        while (remaining > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int read = source.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining));
            if (read == 0) throw new EndOfStreamException("Telemetry source ended while being compressed.");
            hash.AppendData(buffer, 0, read);
            destination.Write(buffer, 0, read);
            remaining -= read;
        }
    }

    static bool MatchesCompressedArchive(string archivePath, byte[] expectedHash, long expectedLength,
        CancellationToken cancellationToken)
    {
        using var compressed = new FileStream(archivePath, FileMode.Open, FileAccess.Read, FileShare.Read,
            64 * 1024, FileOptions.SequentialScan);
        using var gzip = new GZipStream(compressed, CompressionMode.Decompress, leaveOpen: false);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = new byte[64 * 1024];
        long length = 0;
        int read;
        while ((read = gzip.Read(buffer, 0, buffer.Length)) > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            length += read;
            if (length > expectedLength) return false;
            hash.AppendData(buffer, 0, read);
        }
        return length == expectedLength && CryptographicOperations.FixedTimeEquals(hash.GetHashAndReset(), expectedHash);
    }

    static bool MatchesHashAndLength(string path, byte[] expectedHash, long expectedLength,
        CancellationToken cancellationToken)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            64 * 1024, FileOptions.SequentialScan);
        return MatchesHashAndLength(file, expectedHash, expectedLength, cancellationToken);
    }

    static bool MatchesHashAndLength(Stream source, byte[] expectedHash, long expectedLength,
        CancellationToken cancellationToken)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = new byte[64 * 1024];
        long length = 0;
        int read;
        while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            length += read;
            if (length > expectedLength) return false;
            hash.AppendData(buffer, 0, read);
        }
        return length == expectedLength && CryptographicOperations.FixedTimeEquals(hash.GetHashAndReset(), expectedHash);
    }

    static (byte[] Hash, long Length) FingerprintFile(string path, CancellationToken cancellationToken)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            64 * 1024, FileOptions.SequentialScan);
        long expectedLength = file.Length;
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = new byte[64 * 1024];
        long length = 0;
        int read;
        while ((read = file.Read(buffer, 0, buffer.Length)) > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            length += read;
            if (length > expectedLength)
                throw new IOException("Telemetry source changed while it was being fingerprinted.");
            hash.AppendData(buffer, 0, read);
        }
        if (length != expectedLength)
            throw new IOException("Telemetry source changed while it was being fingerprinted.");
        return (hash.GetHashAndReset(), length);
    }

    static bool RestoreClaim(string sourcePath, string claimedSource)
    {
        if (!File.Exists(claimedSource)) return true;
        if (File.Exists(sourcePath) || Directory.Exists(sourcePath)) return false;
        try { File.Move(claimedSource, sourcePath, false); return true; }
        catch (Exception ex) when (IsRecoverable(ex))
        {
            // Leaving the same-directory claim in place preserves the data
            // for manual recovery and avoids overwriting a replacement file.
            return false;
        }
    }

    void ReportFailure(string path, Exception exception)
    {
        var wrapped = new IOException($"Telemetry compression failed for {Path.GetFileName(path)}: {exception.Message}", exception);
        try
        {
            if (onFailure is not null) onFailure(wrapped);
            else Console.Error.WriteLine("ConnectorWatch telemetry compression: " + wrapped.Message);
        }
        catch { /* diagnostic logging is best effort */ }
    }

    static void TryDeleteTemporary(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception ex) when (IsRecoverable(ex)) { }
    }

    static bool TryGetTelemetryDate(string path, out DateOnly date)
    {
        string name = Path.GetFileName(path);
        const string prefix = "telemetry-";
        const string suffix = ".csv";
        if (!name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ||
            !name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
        {
            date = default;
            return false;
        }
        string value = name[prefix.Length..^suffix.Length];
        return DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture,
            DateTimeStyles.None, out date);
    }

    static bool IsRecoverable(Exception ex) => ex is IOException or UnauthorizedAccessException or
        InvalidDataException or EndOfStreamException or SecurityException or ArgumentException;

    public void Dispose()
    {
        Task? running;
        CancellationTokenSource? source;
        lock (lifetimeGate)
        {
            source = lifetime;
            running = worker;
            lifetime = null;
            worker = null;
            source?.Cancel();
        }
        if (running is not null)
        {
            try { running.GetAwaiter().GetResult(); }
            catch (Exception ex) when (IsRecoverable(ex)) { ReportFailure(directory, ex); }
        }
        source?.Dispose();
    }

    enum TelemetryCompressionResult { Compressed, Conflict, Failed }
}

public readonly record struct TelemetryCompressionReport(
    int Candidates, int Compressed, int Conflicts, int Failures);
