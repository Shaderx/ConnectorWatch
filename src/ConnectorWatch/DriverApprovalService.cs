using System.Net;
using System.Net.Http.Headers;
using System.Diagnostics;
using System.Text.Json;

namespace ConnectorWatch;

public sealed class DriverApprovalOptions
{
    public Uri CatalogEndpoint { get; init; } = new(
        "https://github.com/Shaderx/ConnectorWatch/releases/download/driver-catalog/catalog-current.json");
    public IReadOnlyList<SignedMetadataKey> TrustKeys { get; init; } = Array.Empty<SignedMetadataKey>();
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(15);
    public TimeSpan RefreshInterval { get; init; } = TimeSpan.FromHours(6);
    public TimeSpan MaximumRefreshJitter { get; init; } = TimeSpan.FromMinutes(15);
    public int MaximumAttempts { get; init; } = 2;
    public string? BundledCatalogPath { get; init; }
    public TimeSpan ShutdownWaitTimeout { get; init; } = TimeSpan.FromSeconds(3);

    internal void Validate()
    {
        if (!CatalogEndpoint.IsAbsoluteUri || CatalogEndpoint.Scheme != Uri.UriSchemeHttps)
            throw new ArgumentException("The driver catalog endpoint must be an absolute HTTPS URI.");
        if (RequestTimeout < TimeSpan.FromSeconds(1) || RequestTimeout > TimeSpan.FromMinutes(1))
            throw new ArgumentOutOfRangeException(nameof(RequestTimeout));
        if (RefreshInterval < TimeSpan.FromMinutes(1) || RefreshInterval > TimeSpan.FromDays(1))
            throw new ArgumentOutOfRangeException(nameof(RefreshInterval));
        if (MaximumRefreshJitter < TimeSpan.Zero || MaximumRefreshJitter > RefreshInterval / 4)
            throw new ArgumentOutOfRangeException(nameof(MaximumRefreshJitter));
        if (MaximumAttempts is < 1 or > 3)
            throw new ArgumentOutOfRangeException(nameof(MaximumAttempts));
        if (ShutdownWaitTimeout < TimeSpan.FromMilliseconds(100) ||
            ShutdownWaitTimeout > TimeSpan.FromSeconds(30))
            throw new ArgumentOutOfRangeException(nameof(ShutdownWaitTimeout));
    }
}

public enum DriverCatalogRefreshStatus { Updated, NotModified, Skipped, Failed }

public sealed record DriverCatalogRefreshResult(
    DriverCatalogRefreshStatus Status,
    DateTimeOffset CheckedUtc,
    long CatalogRevision,
    string? Error = null);

/// <summary>Owns authenticated driver-catalog refresh, rollback state, and exact-scope decisions.</summary>
public sealed class DriverApprovalService : IDisposable, IAsyncDisposable
{
    public const string EmbeddedReaderProfile = DirectNvRails.ReaderProfile;
    public const string EmbeddedReaderVersion = DirectNvRails.ReaderVersion;
    public static string ApplicationVersion => DirectNvRails.AppVersion;
    private const string CacheFileName = "driver-catalog.signed.json";
    private const string WatermarkFileName = "driver-catalog.watermark.json";
    private const string EtagFileName = "driver-catalog.etag";
    private readonly object _gate = new();
    private readonly string _dataDirectory;
    private readonly DriverApprovalOptions _options;
    private readonly SignedMetadataVerifier _verifier;
    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;
    private readonly TimeProvider _timeProvider;
    private readonly CancellationTokenSource _dispose = new();
    private Task<DriverCatalogRefreshResult>? _inflightRefresh;
    private Task? _refreshLoop;
    private DriverCatalog? _catalog;
    private SignedMetadataWatermark? _watermark;
    private string? _etag;
    private DateTimeOffset? _lastCheckedUtc;
    private DateTimeOffset _nextRefreshUtc;
    private string? _lastError;
    private bool _persistentStateBlocked;
    private bool _persistentLoadPending;
    private bool _disposed;

    public DriverApprovalService(string dataDirectory, DriverApprovalOptions options,
        HttpClient? httpClient = null, TimeProvider? timeProvider = null,
        bool startBackgroundRefresh = false)
    {
        if (string.IsNullOrWhiteSpace(dataDirectory))
            throw new ArgumentException("A driver catalog data directory is required.", nameof(dataDirectory));
        _dataDirectory = Path.GetFullPath(dataDirectory);
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _options.Validate();
        _verifier = new SignedMetadataVerifier(options.TrustKeys);
        _timeProvider = timeProvider ?? TimeProvider.System;
        _httpClient = httpClient ?? new HttpClient();
        _ownsHttpClient = httpClient is null;
        Directory.CreateDirectory(_dataDirectory);
        LoadPersistentState();
        _nextRefreshUtc = _timeProvider.GetUtcNow();
        if (startBackgroundRefresh)
            _refreshLoop = RunRefreshLoopAsync(_dispose.Token);
    }

    public static DriverApprovalService CreateDefault(string dataDirectory) =>
        new(dataDirectory, new DriverApprovalOptions
        {
            TrustKeys = DriverCatalogReleaseTrust.Keys,
            BundledCatalogPath = Path.Combine(AppContext.BaseDirectory, "trust", CacheFileName),
        },
            startBackgroundRefresh: true);

    public DateTimeOffset? LastCheckedUtc { get { lock (_gate) return _lastCheckedUtc; } }
    public DateTimeOffset NextRefreshUtc { get { lock (_gate) return _nextRefreshUtc; } }
    public string? LastError { get { lock (_gate) return _lastError; } }
    public long CatalogRevision { get { lock (_gate) return _catalog?.CatalogRevision ?? 0; } }
    public DateTimeOffset? CatalogExpiresUtc { get { lock (_gate) return _catalog?.ExpiresUtc; } }

    public Task<DriverCatalogRefreshResult> RefreshAsync(bool force,
        CancellationToken cancellationToken = default)
    {
        Task<DriverCatalogRefreshResult> refresh;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var now = _timeProvider.GetUtcNow();
            if (!force && now < _nextRefreshUtc)
                return Task.FromResult(new DriverCatalogRefreshResult(DriverCatalogRefreshStatus.Skipped,
                    now, _catalog?.CatalogRevision ?? 0));
            if (_inflightRefresh is null || _inflightRefresh.IsCompleted)
                _inflightRefresh = RefreshAndClearAsync();
            refresh = _inflightRefresh;
        }
        return cancellationToken.CanBeCanceled ? refresh.WaitAsync(cancellationToken) : refresh;
    }

    public DriverApprovalDecision Decide(DriverIdentity identity, string profile, string readerVersion,
        string appVersion, bool developerMode = false)
    {
        ArgumentNullException.ThrowIfNull(identity);
        if (string.IsNullOrWhiteSpace(profile) || string.IsNullOrWhiteSpace(readerVersion) ||
            string.IsNullOrWhiteSpace(appVersion))
            throw new ArgumentException("Reader profile and reader/application versions are required.");
        // Validate the version syntax before using it in a signed range decision.
        VersionRange.Contains(readerVersion, readerVersion, IncrementPatch(readerVersion));
        VersionRange.Contains(appVersion, appVersion, IncrementPatch(appVersion));
        DriverCatalog? catalog;
        bool persistentStateBlocked;
        bool persistentLoadPending;
        lock (_gate)
        {
            catalog = _catalog;
            persistentStateBlocked = _persistentStateBlocked;
            persistentLoadPending = _persistentLoadPending;
        }
        var now = _timeProvider.GetUtcNow();

        if (persistentStateBlocked)
            return new DriverApprovalDecision(DriverApprovalState.ApprovalUnavailable, false,
                "Driver approval persistent trust state is malformed; restore or reinstall it before private acquisition.",
                catalog?.CatalogRevision ?? 0, catalog?.ExpiresUtc, false,
                identity, profile, readerVersion, appVersion);

        if (persistentLoadPending)
            return new DriverApprovalDecision(DriverApprovalState.ApprovalUnavailable, false,
                "Driver approval persistent trust state is waiting for cache reconciliation; refresh before private acquisition.",
                0, null, false, identity, profile, readerVersion, appVersion);

        if (catalog is null)
            return MissingDecision("Driver approval is unavailable; refresh the signed driver catalog.",
                DriverApprovalState.ApprovalUnavailable, identity, profile, readerVersion, appVersion, developerMode);

        var entry = catalog.Entries.FirstOrDefault(item => item.MatchesIdentity(identity, profile));
        if (entry?.Decision == "revoked")
            return Decision(DriverApprovalState.Revoked, false, entry.Rationale, catalog, false,
                identity, profile, readerVersion, appVersion);

        if (catalog.ExpiresUtc <= now)
            return MissingDecision("Driver approval is unavailable because the signed catalog expired; refresh approvals.",
                DriverApprovalState.ApprovalUnavailable, identity, profile, readerVersion, appVersion,
                developerMode, catalog);
        if (entry is null)
            return MissingDecision("This driver is awaiting maintainer approval.", DriverApprovalState.NotListed,
                identity, profile, readerVersion, appVersion, developerMode, catalog);
        if (!entry.SupportsReader(readerVersion))
        {
            var state = entry.ReaderIsOlderThanMinimum(readerVersion)
                ? DriverApprovalState.RequiresNewerReader : DriverApprovalState.NotListed;
            var reason = state == DriverApprovalState.RequiresNewerReader
                ? $"Reader {readerVersion} is older than the approved range; install a compatible application update."
                : $"Reader {readerVersion} is newer than the tested catalog scope.";
            return Decision(state, false, reason, catalog, false,
                identity, profile, readerVersion, appVersion);
        }
        if (!entry.SupportsApp(appVersion))
        {
            var state = entry.AppIsOlderThanMinimum(appVersion)
                ? DriverApprovalState.RequiresNewerApp : DriverApprovalState.NotListed;
            var reason = state == DriverApprovalState.RequiresNewerApp
                ? $"Application {appVersion} is older than the approved range; install a compatible update."
                : $"Application {appVersion} is newer than the tested catalog scope.";
            return Decision(state, false, reason, catalog, false,
                identity, profile, readerVersion, appVersion);
        }
        return Decision(DriverApprovalState.Approved, true, entry.Rationale, catalog, false,
            identity, profile, readerVersion, appVersion);
    }

    private DriverApprovalDecision MissingDecision(string reason, DriverApprovalState state,
        DriverIdentity identity, string profile, string readerVersion, string appVersion,
        bool developerMode, DriverCatalog? catalog = null)
    {
        if (developerMode)
            return new DriverApprovalDecision(DriverApprovalState.Approved, true,
                "Developer mode bypassed a missing catalog approval; this session is unvalidated.",
                catalog?.CatalogRevision ?? 0, catalog?.ExpiresUtc, true,
                identity, profile, readerVersion, appVersion);
        return new DriverApprovalDecision(state, false, reason, catalog?.CatalogRevision ?? 0,
            catalog?.ExpiresUtc, false, identity, profile, readerVersion, appVersion);
    }

    private static DriverApprovalDecision Decision(DriverApprovalState state, bool mayStart, string reason,
        DriverCatalog catalog, bool unvalidated, DriverIdentity identity, string profile,
        string readerVersion, string appVersion) => new(state, mayStart, reason, catalog.CatalogRevision,
            catalog.ExpiresUtc, unvalidated, identity, profile, readerVersion, appVersion);

    private async Task<DriverCatalogRefreshResult> RefreshAndClearAsync()
    {
        try { return await RefreshCoreAsync(_dispose.Token).ConfigureAwait(false); }
        finally { lock (_gate) _inflightRefresh = null; }
    }

    private async Task<DriverCatalogRefreshResult> RefreshCoreAsync(CancellationToken cancellationToken)
    {
        var now = _timeProvider.GetUtcNow();
        if (_persistentStateBlocked)
            return RecordFailure(now, "Driver catalog persistent trust state is malformed; restore or reinstall the application data before refreshing.");
        if (_options.TrustKeys.Count == 0)
            return RecordFailure(now, "No release driver-catalog trust key is installed in this application build.");

        Exception? lastTransportError = null;
        for (var attempt = 1; attempt <= _options.MaximumAttempts; attempt++)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, _options.CatalogEndpoint);
                string? etag;
                lock (_gate) etag = _catalog is null ? null : _etag;
                if (!string.IsNullOrWhiteSpace(etag))
                    request.Headers.TryAddWithoutValidation("If-None-Match", etag);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(_options.RequestTimeout);
                using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead,
                    timeout.Token).ConfigureAwait(false);
                var finalUri = response.RequestMessage?.RequestUri;
                if (finalUri is null || finalUri.Scheme != Uri.UriSchemeHttps)
                    throw new InvalidDataException("Driver catalog retrieval redirected outside HTTPS.");
                now = _timeProvider.GetUtcNow();
                if (response.StatusCode == HttpStatusCode.NotModified)
                {
                    lock (_gate)
                    {
                        if (_catalog is null)
                            return RecordFailure(now, "Catalog server returned not-modified without a verified cache.");
                        RecordSuccessLocked(now, response.Headers.ETag?.ToString() ?? _etag);
                        return new DriverCatalogRefreshResult(DriverCatalogRefreshStatus.NotModified, now,
                            _catalog.CatalogRevision);
                    }
                }
                response.EnsureSuccessStatusCode();
                if (response.Content.Headers.ContentLength is long length &&
                    length > SignedMetadataVerifier.MaximumEnvelopeBytes)
                    throw new InvalidDataException("Driver catalog response exceeds the download limit.");
                var envelope = await ReadBoundedAsync(response.Content, timeout.Token).ConfigureAwait(false);
                var verified = _verifier.VerifyEnvelope(envelope);
                var catalog = DriverCatalogPayload.ParseVerified(verified,
                    new HashSet<string>(StringComparer.Ordinal) { EmbeddedReaderProfile });
                var header = SignedMetadataHeader.Parse(verified, DriverCatalogPayload.PayloadType,
                    "catalog_revision");
                // A handler may complete despite cancellation. Do not let its response cross the
                // persistent acceptance boundary while an orderly session restart is in progress.
                cancellationToken.ThrowIfCancellationRequested();
                SignedMetadataWatermark nextWatermark;
                using (AcquireCacheLock(cancellationToken))
                {
                    // Re-read under the cross-process lock. Another daemon may have accepted a
                    // higher revision after this request started.
                    SignedMetadataWatermark? diskWatermark;
                    try { diskWatermark = ReadWatermark(); }
                    catch
                    {
                        lock (_gate)
                        {
                            _persistentStateBlocked = true;
                            _catalog = null;
                        }
                        throw;
                    }
                    SignedMetadataWatermark? watermark;
                    lock (_gate)
                    {
                        watermark = HigherWatermark(_watermark, diskWatermark);
                        _watermark = watermark;
                        if (watermark is not null && _catalog is not null &&
                            _catalog.CatalogRevision < watermark.Revision)
                            _catalog = null;
                    }
                    SignedMetadataPolicy.Validate(header, verified, watermark, now);
                    nextWatermark = SignedMetadataPolicy.NextWatermark(header, verified, watermark);
                    cancellationToken.ThrowIfCancellationRequested();
                    try { PersistAccepted(envelope, nextWatermark, response.Headers.ETag?.ToString()); }
                    catch
                    {
                        // PersistAccepted writes the watermark first. Fail closed if replacement of
                        // the cached envelope then fails; the old catalog must not outrank the watermark.
                        lock (_gate)
                        {
                            _watermark = nextWatermark;
                            _catalog = catalog;
                        }
                        throw;
                    }
                }
                lock (_gate)
                {
                    _catalog = catalog;
                    _watermark = nextWatermark;
                    _persistentLoadPending = false;
                    RecordSuccessLocked(now, response.Headers.ETag?.ToString());
                }
                return new DriverCatalogRefreshResult(DriverCatalogRefreshStatus.Updated, now,
                    catalog.CatalogRevision);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException &&
                                       !cancellationToken.IsCancellationRequested)
            {
                lastTransportError = ex;
                if (attempt < _options.MaximumAttempts)
                    await Task.Delay(TimeSpan.FromMilliseconds(250 * attempt), _timeProvider,
                        cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is InvalidDataException or JsonException)
            {
                return RecordFailure(_timeProvider.GetUtcNow(), ex.Message);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return RecordFailure(_timeProvider.GetUtcNow(),
                    "Driver catalog cache could not be updated: " + ex.Message);
            }
        }
        return RecordFailure(_timeProvider.GetUtcNow(),
            lastTransportError?.Message ?? "Driver catalog refresh failed.");
    }

    private async Task RunRefreshLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await RefreshAsync(force: false, cancellationToken).ConfigureAwait(false);
                DateTimeOffset next;
                lock (_gate) next = _nextRefreshUtc;
                var delay = next - _timeProvider.GetUtcNow();
                if (delay < TimeSpan.FromSeconds(1)) delay = TimeSpan.FromSeconds(1);
                await Task.Delay(delay, _timeProvider, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { break; }
            catch
            {
                await Task.Delay(TimeSpan.FromMinutes(1), _timeProvider, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private DriverCatalogRefreshResult RecordFailure(DateTimeOffset now, string error)
    {
        lock (_gate)
        {
            _lastCheckedUtc = now;
            _lastError = error;
            ScheduleNextLocked(now);
            return new DriverCatalogRefreshResult(DriverCatalogRefreshStatus.Failed, now,
                _catalog?.CatalogRevision ?? 0, error);
        }
    }

    private void RecordSuccessLocked(DateTimeOffset now, string? etag)
    {
        _lastCheckedUtc = now;
        _lastError = null;
        if (!string.IsNullOrWhiteSpace(etag) && etag.Length <= 256) _etag = etag;
        ScheduleNextLocked(now);
    }

    private void ScheduleNextLocked(DateTimeOffset now)
    {
        var jitterTicks = _options.MaximumRefreshJitter.Ticks;
        var jitter = jitterTicks == 0 ? 0 : Random.Shared.NextInt64(-jitterTicks, jitterTicks + 1);
        _nextRefreshUtc = now + _options.RefreshInterval + TimeSpan.FromTicks(jitter);
    }

    private void LoadPersistentState()
    {
        try
        {
            // Read the watermark and envelope as one cache transaction. A replacement daemon can
            // otherwise observe the watermark-first commit while the previous daemon is still
            // replacing the matching envelope and mistake orderly restart overlap for corruption.
            using (AcquireCacheLock()) LoadPersistentStateUnderCacheLock();
        }
        catch (CacheLockUnavailableException ex)
        {
            // Lock contention is transient, not evidence that persisted trust state is malformed.
            // Stay unavailable until a later refresh can acquire the lock and revalidate it.
            _catalog = null;
            _persistentLoadPending = true;
            _lastError = "Driver catalog startup is waiting for another orderly cache writer: " + ex.Message;
        }
    }

    private void LoadPersistentStateUnderCacheLock()
    {
        try { _watermark = ReadWatermark(); }
        catch (Exception ex)
        {
            _persistentStateBlocked = true;
            _lastError = "Driver catalog rollback watermark is invalid: " + ex.Message;
            return;
        }
        DriverCatalog? bestCatalog = null;
        VerifiedSignedMetadata? bestVerified = null;
        byte[]? bestEnvelope = null;
        try
        {
            var cachePath = Path.Combine(_dataDirectory, CacheFileName);
            if (File.Exists(cachePath))
            {
                (bestCatalog, bestVerified, bestEnvelope) = ReadCatalogFile(cachePath);
                var etagPath = Path.Combine(_dataDirectory, EtagFileName);
                if (File.Exists(etagPath) && new FileInfo(etagPath).Length is > 0 and <= 256)
                {
                    var etag = File.ReadAllText(etagPath).Trim();
                    if (etag.Length is > 0 and <= 256) _etag = etag;
                }
            }
        }
        catch (Exception ex)
        {
            _lastError = "Verified driver catalog cache is unavailable: " + ex.Message;
        }
        if (!string.IsNullOrWhiteSpace(_options.BundledCatalogPath) && File.Exists(_options.BundledCatalogPath))
        {
            try
            {
                var (bundledCatalog, bundledVerified, bundledEnvelope) = ReadCatalogFile(_options.BundledCatalogPath);
                if (bestCatalog is null || bundledCatalog.CatalogRevision > bestCatalog.CatalogRevision)
                    (bestCatalog, bestVerified, bestEnvelope) = (bundledCatalog, bundledVerified, bundledEnvelope);
            }
            catch (Exception ex)
            {
                _lastError ??= "Bundled driver catalog is unavailable: " + ex.Message;
            }
        }
        if (bestCatalog is not null && bestVerified is not null && bestEnvelope is not null)
        {
            try
            {
                var diskWatermark = ReadWatermark();
                var watermark = HigherWatermark(_watermark, diskWatermark);
                SignedMetadataPolicy.Validate(bestCatalog.Header, bestVerified, watermark,
                    _timeProvider.GetUtcNow(), allowExpired: true);
                var nextWatermark = SignedMetadataPolicy.NextWatermark(
                    bestCatalog.Header, bestVerified, watermark);
                // Persist even a bundled catalog before it can authorize native access. The
                // watermark in the data directory then survives an application/bundle rollback.
                PersistAccepted(bestEnvelope, nextWatermark, etag: null);
                _catalog = bestCatalog;
                _watermark = nextWatermark;
            }
            catch (Exception ex)
            {
                _catalog = null;
                _persistentStateBlocked = true;
                _lastError = "Driver catalog startup state could not be committed: " + ex.Message;
            }
        }
        else if (_watermark is not null)
        {
            _persistentStateBlocked = true;
            _lastError ??= "Driver catalog cache is missing or invalid below the persistent rollback watermark.";
        }
    }

    private (DriverCatalog Catalog, VerifiedSignedMetadata Verified, byte[] Envelope) ReadCatalogFile(string path)
    {
        var length = new FileInfo(path).Length;
        if (length is < 1 or > SignedMetadataVerifier.MaximumEnvelopeBytes)
            throw new InvalidDataException("Signed driver catalog cache is empty or oversized.");
        var envelope = File.ReadAllBytes(path);
        var verified = _verifier.VerifyEnvelope(envelope);
        var catalog = DriverCatalogPayload.ParseVerified(verified,
            new HashSet<string>(StringComparer.Ordinal) { EmbeddedReaderProfile });
        var header = SignedMetadataHeader.Parse(verified, DriverCatalogPayload.PayloadType,
            "catalog_revision");
        SignedMetadataPolicy.Validate(header, verified, _watermark, _timeProvider.GetUtcNow(), allowExpired: true);
        return (catalog, verified, envelope);
    }

    private SignedMetadataWatermark? ReadWatermark()
    {
        var path = Path.Combine(_dataDirectory, WatermarkFileName);
        if (!File.Exists(path)) return null;
        if (new FileInfo(path).Length is < 1 or > 4096)
            throw new InvalidDataException("Rollback watermark is empty or oversized.");
        var bytes = File.ReadAllBytes(path);
        using var document = StrictJson.ParseObject(bytes, "driver catalog rollback watermark");
        StrictJson.RequireOnlyProperties(document.RootElement, "driver catalog rollback watermark",
            "revision", "payload_sha256");
        var revision = StrictJson.RequiredInt64(document.RootElement, "revision");
        var digest = StrictJson.RequiredString(document.RootElement, "payload_sha256", 64);
        SignedMetadataPolicy.ValidateDigest(digest);
        if (revision < 1) throw new InvalidDataException("Rollback watermark revision must be positive.");
        return new SignedMetadataWatermark(revision, digest);
    }

    private void PersistAccepted(byte[] envelope, SignedMetadataWatermark watermark, string? etag)
    {
        var watermarkBytes = JsonSerializer.SerializeToUtf8Bytes(new
        {
            revision = watermark.Revision,
            payload_sha256 = watermark.PayloadSha256,
        });
        AtomicWrite(Path.Combine(_dataDirectory, WatermarkFileName), watermarkBytes);
        AtomicWrite(Path.Combine(_dataDirectory, CacheFileName), envelope);
        if (!string.IsNullOrWhiteSpace(etag) && etag.Length <= 256)
            AtomicWrite(Path.Combine(_dataDirectory, EtagFileName), System.Text.Encoding.UTF8.GetBytes(etag));
    }

    private FileStream AcquireCacheLock(CancellationToken cancellationToken = default)
    {
        var path = Path.Combine(_dataDirectory, "driver-catalog.lock");
        var elapsed = Stopwatch.StartNew();
        IOException? last = null;
        while (elapsed.Elapsed < _options.ShutdownWaitTimeout)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite,
                    FileShare.None, bufferSize: 1, FileOptions.WriteThrough);
            }
            catch (IOException ex)
            {
                last = ex;
                if (cancellationToken.WaitHandle.WaitOne(TimeSpan.FromMilliseconds(50)))
                    cancellationToken.ThrowIfCancellationRequested();
            }
        }
        throw new CacheLockUnavailableException("Timed out waiting for the driver catalog cache lock.", last);
    }

    private static SignedMetadataWatermark? HigherWatermark(SignedMetadataWatermark? first,
        SignedMetadataWatermark? second)
    {
        if (first is null) return second;
        if (second is null) return first;
        if (first.Revision != second.Revision) return first.Revision > second.Revision ? first : second;
        if (!string.Equals(first.PayloadSha256, second.PayloadSha256, StringComparison.Ordinal))
            throw new InvalidDataException("Persistent driver catalog watermark equivocation was detected.");
        return first;
    }

    private static void AtomicWrite(string path, byte[] bytes)
    {
        var temporary = Path.Combine(Path.GetDirectoryName(path)!, "." + Path.GetFileName(path) +
            "." + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            File.WriteAllBytes(temporary, bytes);
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); } catch { }
        }
    }

    private static async Task<byte[]> ReadBoundedAsync(HttpContent content, CancellationToken cancellationToken)
    {
        await using var stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var output = new MemoryStream();
        var buffer = new byte[16 * 1024];
        while (true)
        {
            var read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            if (output.Length + read > SignedMetadataVerifier.MaximumEnvelopeBytes)
                throw new InvalidDataException("Driver catalog response exceeds the download limit.");
            output.Write(buffer, 0, read);
        }
        return output.ToArray();
    }

    private static string IncrementPatch(string version)
    {
        var parts = version.Split('.');
        if (parts.Length is < 2 or > 4 || !int.TryParse(parts[^1], out var tail) || tail == int.MaxValue)
            throw new InvalidDataException($"Version '{version}' is invalid.");
        parts[^1] = (tail + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);
        return string.Join('.', parts);
    }

    public void Dispose()
    {
        Task[] pending;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            pending = PendingShutdownTasksLocked();
        }
        _dispose.Cancel();
        if (_ownsHttpClient) _httpClient.Dispose();
        FinishSynchronousShutdown(pending);
    }

    public async ValueTask DisposeAsync()
    {
        Task[] pending;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            pending = PendingShutdownTasksLocked();
        }
        _dispose.Cancel();
        if (_ownsHttpClient) _httpClient.Dispose();
        var completion = Task.WhenAll(pending);
        try { await completion.WaitAsync(_options.ShutdownWaitTimeout).ConfigureAwait(false); }
        catch (Exception ex) when (ex is OperationCanceledException or TimeoutException || completion.IsCompleted)
        {
            // Cancellation, a recorded refresh failure, or the shutdown bound ends disposal.
        }
        DisposeTokenSourceWhenComplete(completion);
    }

    private Task[] PendingShutdownTasksLocked() => new[] { _refreshLoop, _inflightRefresh }
        .Where(task => task is not null && !task.IsCompleted).Cast<Task>().Distinct().ToArray();

    private void FinishSynchronousShutdown(Task[] pending)
    {
        var completion = Task.WhenAll(pending);
        try { _ = completion.Wait(_options.ShutdownWaitTimeout); }
        catch (AggregateException) { }
        DisposeTokenSourceWhenComplete(completion);
    }

    private void DisposeTokenSourceWhenComplete(Task completion)
    {
        if (completion.IsCompleted)
        {
            _dispose.Dispose();
            return;
        }
        _ = completion.ContinueWith(_ => _dispose.Dispose(), CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    private sealed class CacheLockUnavailableException(string message, Exception? innerException)
        : IOException(message, innerException);
}

/// <summary>
/// Release builds inject pinned catalog public keys by providing an implementation of
/// AddReleaseKeys in a generated, authenticated source file. This repository intentionally
/// contains no claimed maintainer trust anchor.
/// </summary>
internal static partial class DriverCatalogReleaseTrust
{
    public static IReadOnlyList<SignedMetadataKey> Keys
    {
        get
        {
            var keys = new List<SignedMetadataKey>();
            AddReleaseKeys(keys);
            return keys.AsReadOnly();
        }
    }

    static partial void AddReleaseKeys(List<SignedMetadataKey> keys);
}
