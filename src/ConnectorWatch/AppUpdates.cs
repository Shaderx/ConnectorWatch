using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Collections.Concurrent;

namespace ConnectorWatch;

public enum AppUpdateDisposition
{
    Current,
    Available,
    RollbackNeedsConfirmation,
    IncompatibleDataSchema,
}

public sealed record AppRollbackPolicy(
    [property: JsonPropertyName("allowed")] bool Allowed,
    [property: JsonPropertyName("minimum_source_version")] string? MinimumSourceVersion,
    [property: JsonPropertyName("maximum_source_version")] string? MaximumSourceVersion,
    [property: JsonPropertyName("minimum_data_schema")] int MinimumDataSchema,
    [property: JsonPropertyName("maximum_data_schema")] int MaximumDataSchema);

public sealed record AppReleaseMetadata(
    [property: JsonPropertyName("payload_type")] string PayloadType,
    [property: JsonPropertyName("schema_version")] int SchemaVersion,
    [property: JsonPropertyName("revision")] long Revision,
    [property: JsonPropertyName("issued_utc")] DateTimeOffset IssuedUtc,
    [property: JsonPropertyName("expires_utc")] DateTimeOffset ExpiresUtc,
    [property: JsonPropertyName("version")] string Version,
    [property: JsonPropertyName("installer_url")] string InstallerUrl,
    [property: JsonPropertyName("installer_sha256")] string InstallerSha256,
    [property: JsonPropertyName("installer_size")] long InstallerSize,
    [property: JsonPropertyName("release_notes")] string ReleaseNotes,
    [property: JsonPropertyName("minimum_data_schema")] int MinimumDataSchema,
    [property: JsonPropertyName("maximum_data_schema")] int MaximumDataSchema,
    [property: JsonPropertyName("rollback")] AppRollbackPolicy? Rollback);

public sealed class AppUpdateCheckResult
{
    internal AppUpdateCheckResult(AppUpdateDisposition disposition, AppReleaseMetadata release,
        string detail, bool requiresRestart, string authenticatedPayloadSha256, long authenticatedRevision,
        DateTimeOffset authenticatedExpiresUtc)
    {
        Disposition = disposition;
        Release = release;
        Detail = detail;
        RequiresRestart = requiresRestart;
        AuthenticatedPayloadSha256 = authenticatedPayloadSha256;
        AuthenticatedRevision = authenticatedRevision;
        AuthenticatedExpiresUtc = authenticatedExpiresUtc;
    }
    public AppUpdateDisposition Disposition { get; }
    public AppReleaseMetadata Release { get; }
    public string Detail { get; }
    public bool RequiresRestart { get; }
    internal string AuthenticatedPayloadSha256 { get; }
    internal long AuthenticatedRevision { get; }
    internal DateTimeOffset AuthenticatedExpiresUtc { get; }
}

public sealed record AppUpdateOptions(
    Uri MetadataUri,
    string CacheDirectory,
    IReadOnlySet<string> PublisherCertificateSha256,
    long MaximumInstallerBytes = 512L * 1024 * 1024,
    int MaximumMetadataBytes = 256 * 1024,
    TimeSpan? MetadataTimeout = null,
    TimeSpan? InstallerTimeout = null,
    IReadOnlySet<string>? SelfSignedPublisherCertificateSha256 = null);

public sealed record AppUpdateTrustConfiguration(
    [property: JsonPropertyName("schema_version")] int SchemaVersion,
    [property: JsonPropertyName("metadata_url")] string MetadataUrl,
    [property: JsonPropertyName("metadata_keys")] AppUpdateMetadataKey[] MetadataKeys,
    [property: JsonPropertyName("publisher_certificate_sha256")] string[] PublisherCertificateSha256,
    [property: JsonPropertyName("self_signed_publisher_certificate_sha256")] string[]? SelfSignedPublisherCertificateSha256 = null);

public sealed record AppUpdateMetadataKey(
    [property: JsonPropertyName("key_id")] string KeyId,
    [property: JsonPropertyName("subject_public_key_info_base64")] string SubjectPublicKeyInfoBase64,
    [property: JsonPropertyName("revoked")] bool Revoked = false);

internal static class AppUpdateTrustConfigurationReader
{
    const int MaximumBytes = 32 * 1024;
    const int MaximumEntries = 16;

    public static AppUpdateTrustConfiguration ReadFile(string path)
    {
        using var stream = File.OpenRead(Path.GetFullPath(path));
        if (stream.Length is <= 0 or > MaximumBytes)
            throw new InvalidDataException("Application update trust configuration is empty or oversized.");
        var bytes = new byte[checked((int)stream.Length)];
        stream.ReadExactly(bytes);
        return Parse(bytes);
    }

    internal static AppUpdateTrustConfiguration Parse(ReadOnlySpan<byte> utf8)
    {
        if (utf8.IsEmpty || utf8.Length > MaximumBytes)
            throw new InvalidDataException("Application update trust configuration is empty or oversized.");
        using var document = StrictJson.ParseObject(utf8, "app update trust");
        var root = document.RootElement;
        var properties = new List<string>
        {
            "schema_version", "metadata_url", "metadata_keys", "publisher_certificate_sha256",
        };
        if (root.TryGetProperty("self_signed_publisher_certificate_sha256", out _))
            properties.Add("self_signed_publisher_certificate_sha256");
        StrictJson.RequireOnlyProperties(root, "app update trust", properties.ToArray());

        int schemaVersion = StrictJson.RequiredInt32(root, "schema_version");
        string metadataUrl = StrictJson.RequiredString(root, "metadata_url", 2048);
        var metadataKeys = ReadMetadataKeys(root.GetProperty("metadata_keys"));
        var publicPins = ReadPins(root.GetProperty("publisher_certificate_sha256"));
        string[]? selfSignedPins = root.TryGetProperty("self_signed_publisher_certificate_sha256", out var selfSigned)
            ? ReadPins(selfSigned)
            : null;
        _ = WindowsAuthenticodeVerifier.NormalizeConfiguredPins(publicPins, selfSignedPins);
        return new(schemaVersion, metadataUrl, metadataKeys, publicPins, selfSignedPins);
    }

    static AppUpdateMetadataKey[] ReadMetadataKeys(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Array || element.GetArrayLength() > MaximumEntries)
            throw new InvalidDataException("Application metadata keys must be a bounded array.");
        var keys = new List<AppUpdateMetadataKey>();
        foreach (var item in element.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException("Application metadata key entries must be JSON objects.");
            StrictJson.RequireOnlyProperties(item, "application metadata key",
                "key_id", "subject_public_key_info_base64", "revoked");
            var revoked = item.GetProperty("revoked");
            if (revoked.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                throw new InvalidDataException("Application metadata key revocation flags must be Boolean.");
            keys.Add(new AppUpdateMetadataKey(
                StrictJson.RequiredString(item, "key_id", 64),
                StrictJson.RequiredString(item, "subject_public_key_info_base64", 1024),
                revoked.GetBoolean()));
        }
        return keys.ToArray();
    }

    static string[] ReadPins(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Array || element.GetArrayLength() > MaximumEntries)
            throw new InvalidDataException("Publisher certificate pins must be a bounded array.");
        var pins = new List<string>();
        foreach (var item in element.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String || item.GetString() is not string pin ||
                pin.Length != 64 || pin.Any(character => !char.IsAsciiHexDigit(character)))
                throw new InvalidDataException("Publisher certificate pins must be SHA-256 hexadecimal fingerprints.");
            pins.Add(pin);
        }
        return pins.ToArray();
    }
}

public static class AppReleaseMetadataValidator
{
    public static AppReleaseMetadata ValidateVerifiedRelease(VerifiedSignedMetadata verified)
    {
        ArgumentNullException.ThrowIfNull(verified);
        var header = SignedMetadataHeader.Parse(verified, "app-release");
        var release = JsonSerializer.Deserialize<AppReleaseMetadata>(verified.PayloadBytes.Span,
            new JsonSerializerOptions(JsonSerializerDefaults.Web)
            {
                PropertyNameCaseInsensitive = false,
                AllowTrailingCommas = false,
                ReadCommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 32,
            }) ?? throw new InvalidDataException("App release metadata is empty.");
        AppUpdateService.ValidateRelease(release, header);
        return release;
    }

    public static AppReleaseMetadata ValidateAuthenticatedEnvelope(ReadOnlySpan<byte> envelope,
        SignedMetadataVerifier verifier, DateTimeOffset now, SignedMetadataWatermark? watermark = null)
    {
        ArgumentNullException.ThrowIfNull(verifier);
        var verified = verifier.VerifyEnvelope(envelope);
        var header = SignedMetadataHeader.Parse(verified, "app-release");
        SignedMetadataPolicy.Validate(header, verified, watermark, now);
        return ValidateVerifiedRelease(verified);
    }
}

/// <summary>Authenticated application update discovery, bounded download and verified installer launch.</summary>
public sealed class AppUpdateService
{
    const int CurrentMetadataSchema = 1;
    readonly HttpClient http;
    readonly SignedMetadataVerifier metadataVerifier;
    readonly IAuthenticodeVerifier authenticode;
    readonly AppUpdateOptions options;
    readonly TimeProvider timeProvider;
    readonly SemaphoreSlim operation = new(1, 1);
    readonly ConcurrentDictionary<AppUpdateCheckResult, byte> authenticatedChecks = new();
    readonly JsonSerializerOptions json = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
        AllowTrailingCommas = false,
        ReadCommentHandling = JsonCommentHandling.Disallow,
        MaxDepth = 32,
    };

    public AppUpdateService(HttpClient http, SignedMetadataVerifier metadataVerifier,
        AppUpdateOptions options, IAuthenticodeVerifier? authenticode = null, TimeProvider? timeProvider = null)
    {
        this.http = http ?? throw new ArgumentNullException(nameof(http));
        this.metadataVerifier = metadataVerifier ?? throw new ArgumentNullException(nameof(metadataVerifier));
        this.options = options ?? throw new ArgumentNullException(nameof(options));
        this.authenticode = authenticode ?? new WindowsAuthenticodeVerifier();
        this.timeProvider = timeProvider ?? TimeProvider.System;
        if (options.MetadataUri.Scheme != Uri.UriSchemeHttps) throw new ArgumentException("App update metadata must use HTTPS.", nameof(options));
        if (options.MaximumInstallerBytes <= 0 || options.MaximumMetadataBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(options), "Update size limits must be positive.");
        var publicPins = WindowsAuthenticodeVerifier.NormalizePins(options.PublisherCertificateSha256);
        var selfSignedPins = WindowsAuthenticodeVerifier.NormalizePins(
            options.SelfSignedPublisherCertificateSha256 is null
                ? Enumerable.Empty<string>()
                : options.SelfSignedPublisherCertificateSha256);
        if (options.PublisherCertificateSha256.Count == 0 && (options.SelfSignedPublisherCertificateSha256?.Count ?? 0) == 0)
            throw new ArgumentException("At least one public or self-signed application publisher certificate pin is required.", nameof(options));
        if (publicPins.Overlaps(selfSignedPins))
            throw new ArgumentException("A publisher certificate pin cannot use both public and self-signed trust policies.", nameof(options));
    }

    /// <summary>Loads the app-specific metadata keys and publisher pins shipped in the signed application.</summary>
    public static AppUpdateService CreateDefault(HttpClient http, string? applicationBaseDirectory = null,
        IAuthenticodeVerifier? authenticode = null)
    {
        var path = Path.Combine(applicationBaseDirectory ?? AppContext.BaseDirectory, "app-update-trust.json");
        var trust = AppUpdateTrustConfigurationReader.ReadFile(path);
        if (trust.SchemaVersion != 1 || trust.MetadataKeys.Length == 0)
            throw new InvalidDataException("App update trust configuration is incomplete.");
        var pins = WindowsAuthenticodeVerifier.NormalizeConfiguredPins(
            trust.PublisherCertificateSha256, trust.SelfSignedPublisherCertificateSha256);
        if (!Uri.TryCreate(trust.MetadataUrl, UriKind.Absolute, out var metadataUri) || metadataUri.Scheme != Uri.UriSchemeHttps)
            throw new InvalidDataException("App update metadata URL must be HTTPS.");
        var keys = trust.MetadataKeys.Select(key => new SignedMetadataKey(
            key.KeyId, key.SubjectPublicKeyInfoBase64, key.Revoked)).ToArray();
        return new(http, new SignedMetadataVerifier(keys),
            new AppUpdateOptions(metadataUri, DeploymentPaths.UpdateCacheDirectory(),
                pins.Public,
                SelfSignedPublisherCertificateSha256: pins.SelfSigned), authenticode);
    }

    public async Task<AppUpdateCheckResult> CheckAsync(string currentVersion, int currentDataSchema,
        DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        await operation.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        bounded.CancelAfter(options.MetadataTimeout ?? TimeSpan.FromSeconds(30));
        var envelope = await DownloadBytesBoundedAsync(options.MetadataUri, options.MaximumMetadataBytes, bounded.Token).ConfigureAwait(false);
        var verified = metadataVerifier.VerifyEnvelope(envelope);
        var header = SignedMetadataHeader.Parse(verified, "app-release");
        var watermarkPath = Path.Combine(options.CacheDirectory, "app-release-watermark.json");

        var release = JsonSerializer.Deserialize<AppReleaseMetadata>(verified.PayloadBytes.Span, json)
            ?? throw new InvalidDataException("App release metadata is empty.");
        ValidateRelease(release, header);
        ValidateAndPersistWatermark(watermarkPath, header, verified, now);

        var current = SemanticVersion.Parse(currentVersion);
        var offered = SemanticVersion.Parse(release.Version);
        AppUpdateCheckResult result;
        if (currentDataSchema < release.MinimumDataSchema || currentDataSchema > release.MaximumDataSchema)
            result = new(AppUpdateDisposition.IncompatibleDataSchema, release,
                $"Version {release.Version} supports data schema {release.MinimumDataSchema}-{release.MaximumDataSchema}; this installation uses {currentDataSchema}.", true, verified.PayloadSha256, header.Revision, header.ExpiresUtc);
        else if (offered == current)
            result = new(AppUpdateDisposition.Current, release, "ConnectorWatch is current.", false, verified.PayloadSha256, header.Revision, header.ExpiresUtc);
        else if (offered > current)
            result = new(AppUpdateDisposition.Available, release, $"ConnectorWatch {release.Version} is available.", true, verified.PayloadSha256, header.Revision, header.ExpiresUtc);
        else
        {
            if (!AllowsRollback(release.Rollback, current, currentDataSchema))
                throw new InvalidDataException("Authenticated metadata attempted an undeclared or schema-unsafe application rollback.");
            result = new(AppUpdateDisposition.RollbackNeedsConfirmation, release,
                $"Rollback to {release.Version} requires explicit confirmation because binaries and data schema may move backward.", true, verified.PayloadSha256, header.Revision, header.ExpiresUtc);
        }
        authenticatedChecks.TryAdd(result, 0);
        return result;
        }
        finally { operation.Release(); }
    }

    public async Task<string> DownloadInstallerAsync(AppUpdateCheckResult check,
        CancellationToken cancellationToken = default)
    {
        if (!authenticatedChecks.ContainsKey(check)) throw new InvalidOperationException("Download requires a result returned by this update service.");
        if (check.Disposition is not (AppUpdateDisposition.Available or AppUpdateDisposition.RollbackNeedsConfirmation))
            throw new InvalidOperationException("The authenticated check does not offer an installable application.");
        var release = check.Release;
        ValidateAuthorization(check);
        await operation.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        bounded.CancelAfter(options.InstallerTimeout ?? TimeSpan.FromMinutes(10));
        ValidateInstallerFields(release);
        if (release.InstallerSize > options.MaximumInstallerBytes)
            throw new InvalidDataException("Authenticated installer size exceeds the application download limit.");
        Directory.CreateDirectory(options.CacheDirectory);
        var digest = release.InstallerSha256.ToLowerInvariant();
        var finalPath = Path.Combine(options.CacheDirectory, $"ConnectorWatch-{release.Version}-{digest[..12]}.exe");
        if (File.Exists(finalPath))
        {
            VerifyDownloadedInstaller(finalPath, release);
            return finalPath;
        }
        var partialPath = finalPath + ".partial";
        if (File.Exists(partialPath)) File.Delete(partialPath);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, release.InstallerUrl);
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, bounded.Token).ConfigureAwait(false);
            if (response.StatusCode != HttpStatusCode.OK) throw new HttpRequestException("Installer download returned " + response.StatusCode);
            if (response.Content.Headers.ContentLength is long declared && declared != release.InstallerSize)
                throw new InvalidDataException("Installer Content-Length does not match authenticated metadata.");
            await using var input = await response.Content.ReadAsStreamAsync(bounded.Token).ConfigureAwait(false);
            await using var output = new FileStream(partialPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                1024 * 1024, FileOptions.Asynchronous | FileOptions.WriteThrough);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[1024 * 1024];
            long total = 0;
            while (true)
            {
                int read = await input.ReadAsync(buffer, bounded.Token).ConfigureAwait(false);
                if (read == 0) break;
                total = checked(total + read);
                if (total > release.InstallerSize || total > options.MaximumInstallerBytes)
                    throw new InvalidDataException("Installer download exceeded its authenticated size.");
                hash.AppendData(buffer, 0, read);
                await output.WriteAsync(buffer.AsMemory(0, read), bounded.Token).ConfigureAwait(false);
            }
            await output.FlushAsync(bounded.Token).ConfigureAwait(false);
            output.Flush(flushToDisk: true);
            if (total != release.InstallerSize ||
                !CryptographicOperations.FixedTimeEquals(hash.GetHashAndReset(), Convert.FromHexString(digest)))
                throw new CryptographicException("Installer size or SHA-256 does not match authenticated metadata.");
            output.Close();
            authenticode.Verify(partialPath, options.PublisherCertificateSha256,
                options.SelfSignedPublisherCertificateSha256);
            File.Move(partialPath, finalPath);
            return finalPath;
        }
        catch
        {
            if (File.Exists(partialPath)) File.Delete(partialPath);
            throw;
        }
        }
        finally { operation.Release(); }
    }

    /// <summary>Revalidates the cached file immediately before starting the signed installer.</summary>
    public Process LaunchInstaller(string installerPath, AppUpdateCheckResult check,
        string? configurationPath = null)
    {
        if (!authenticatedChecks.ContainsKey(check)) throw new InvalidOperationException("Launch requires a result returned by this update service.");
        if (check.Disposition is not (AppUpdateDisposition.Available or AppUpdateDisposition.RollbackNeedsConfirmation))
            throw new InvalidOperationException("The authenticated check does not offer an installable application.");
        ValidateAuthorization(check);
        var release = check.Release;
        if (release.InstallerSize > options.MaximumInstallerBytes)
            throw new InvalidDataException("Authenticated installer size exceeds the application download limit.");
        VerifyDownloadedInstaller(installerPath, release);
        var start = new ProcessStartInfo(Path.GetFullPath(installerPath)) { UseShellExecute = true };
        start.ArgumentList.Add("/UPDATE=1");
        if (!string.IsNullOrWhiteSpace(configurationPath))
            start.ArgumentList.Add("/CONFIG=" + Path.GetFullPath(configurationPath));
        return Process.Start(start) ?? throw new InvalidOperationException("Windows did not start the ConnectorWatch installer.");
    }

    void VerifyDownloadedInstaller(string path, AppReleaseMetadata release)
    {
        var info = new FileInfo(path);
        if (!info.Exists || info.Length != release.InstallerSize)
            throw new InvalidDataException("Cached installer size does not match authenticated metadata.");
        using var stream = info.OpenRead();
        var actual = SHA256.HashData(stream);
        if (!CryptographicOperations.FixedTimeEquals(actual, Convert.FromHexString(release.InstallerSha256)))
            throw new CryptographicException("Cached installer digest does not match authenticated metadata.");
        authenticode.Verify(path, options.PublisherCertificateSha256,
            options.SelfSignedPublisherCertificateSha256);
    }

    async Task<byte[]> DownloadBytesBoundedAsync(Uri uri, long maximumBytes, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode != HttpStatusCode.OK) throw new HttpRequestException("Update metadata returned " + response.StatusCode);
        if (response.Content.Headers.ContentLength is long declared && declared > maximumBytes)
            throw new InvalidDataException("Update metadata exceeds its size limit.");
        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var output = new MemoryStream();
        var buffer = new byte[16 * 1024];
        while (true)
        {
            int read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            if (output.Length + read > maximumBytes) throw new InvalidDataException("Update metadata exceeds its size limit.");
            output.Write(buffer, 0, read);
        }
        return output.ToArray();
    }

    internal static void ValidateRelease(AppReleaseMetadata release, SignedMetadataHeader header)
    {
        if (release.PayloadType != "app-release" || release.SchemaVersion != CurrentMetadataSchema ||
            release.Revision != header.Revision || release.IssuedUtc != header.IssuedUtc || release.ExpiresUtc != header.ExpiresUtc)
            throw new InvalidDataException("App release payload header does not match the verified signed header.");
        var releaseVersion = SemanticVersion.Parse(release.Version);
        if (release.ReleaseNotes.Length > 32 * 1024) throw new InvalidDataException("App release notes are too large.");
        if (release.MinimumDataSchema < 1 || release.MaximumDataSchema < release.MinimumDataSchema)
            throw new InvalidDataException("App release data-schema range is invalid.");
        if (release.Rollback is { Allowed: true } rollback)
        {
            if (string.IsNullOrWhiteSpace(rollback.MinimumSourceVersion) ||
                string.IsNullOrWhiteSpace(rollback.MaximumSourceVersion))
                throw new InvalidDataException("Enabled rollback policy requires explicit minimum and maximum source versions.");
            var minimumSource = SemanticVersion.Parse(rollback.MinimumSourceVersion);
            var maximumSource = SemanticVersion.Parse(rollback.MaximumSourceVersion);
            if (minimumSource > maximumSource || minimumSource.CompareTo(releaseVersion) <= 0)
                throw new InvalidDataException("Rollback source-version range must be ordered and strictly newer than the offered version.");
            if (rollback.MinimumDataSchema < release.MinimumDataSchema ||
                rollback.MaximumDataSchema > release.MaximumDataSchema ||
                rollback.MaximumDataSchema < rollback.MinimumDataSchema)
                throw new InvalidDataException("Rollback data-schema range must be ordered and contained within the release data-schema range.");
        }
        ValidateInstallerFields(release);
    }

    static void ValidateInstallerFields(AppReleaseMetadata release)
    {
        if (!Uri.TryCreate(release.InstallerUrl, UriKind.Absolute, out var installerUri) || installerUri.Scheme != Uri.UriSchemeHttps ||
            !string.IsNullOrEmpty(installerUri.UserInfo))
            throw new InvalidDataException("Authenticated installer URL must be HTTPS without embedded credentials.");
        if (release.InstallerSize <= 0 || release.InstallerSha256.Length != 64 ||
            !release.InstallerSha256.All(Uri.IsHexDigit))
            throw new InvalidDataException("Authenticated installer size or SHA-256 is invalid.");
    }

    static bool AllowsRollback(AppRollbackPolicy? policy, SemanticVersion source, int dataSchema)
    {
        if (policy is null || !policy.Allowed ||
            string.IsNullOrWhiteSpace(policy.MinimumSourceVersion) ||
            string.IsNullOrWhiteSpace(policy.MaximumSourceVersion) ||
            dataSchema < policy.MinimumDataSchema || dataSchema > policy.MaximumDataSchema)
            return false;
        return source.CompareTo(SemanticVersion.Parse(policy.MinimumSourceVersion)) >= 0 &&
            source.CompareTo(SemanticVersion.Parse(policy.MaximumSourceVersion)) <= 0;
    }

    void ValidateAuthorization(AppUpdateCheckResult check)
    {
        if (check.AuthenticatedExpiresUtc <= timeProvider.GetUtcNow())
            throw new InvalidDataException("The authenticated app update offer has expired; check again.");
        using var held = AcquireWatermarkLock(Path.Combine(options.CacheDirectory, "app-release-watermark.lock"));
        var watermark = ReadWatermark(Path.Combine(options.CacheDirectory, "app-release-watermark.json"));
        if (watermark is null || watermark.Revision != check.AuthenticatedRevision ||
            !string.Equals(watermark.PayloadSha256, check.AuthenticatedPayloadSha256, StringComparison.Ordinal))
            throw new InvalidDataException("A newer or different authenticated app update superseded this offer; check again.");
    }

    static SignedMetadataWatermark? ReadWatermark(string path)
    {
        if (!File.Exists(path)) return null;
        try
        {
            var value = JsonSerializer.Deserialize<SignedMetadataWatermark>(File.ReadAllBytes(path));
            return value ?? throw new InvalidDataException("App update watermark is invalid.");
        }
        catch (JsonException ex) { throw new InvalidDataException("App update watermark is invalid.", ex); }
    }

    static void ValidateAndPersistWatermark(string path, SignedMetadataHeader header,
        VerifiedSignedMetadata verified, DateTimeOffset now)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var held = AcquireWatermarkLock(Path.Combine(Path.GetDirectoryName(path)!, "app-release-watermark.lock"));
        var current = ReadWatermark(path);
        SignedMetadataPolicy.Validate(header, verified, current, now);
        var watermark = SignedMetadataPolicy.NextWatermark(header, verified, current);
        var temporary = path + ".new-" + Guid.NewGuid().ToString("N");
        using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
            4096, FileOptions.WriteThrough))
        {
            JsonSerializer.Serialize(stream, watermark);
            stream.Flush(flushToDisk: true);
        }
        File.Move(temporary, path, overwrite: true);
    }

    static FileStream AcquireWatermarkLock(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var deadline = Environment.TickCount64 + 10_000;
        while (true)
        {
            try { return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) when (Environment.TickCount64 < deadline) { Thread.Sleep(50); }
        }
    }

    readonly record struct SemanticVersion(int Major, int Minor, int Patch, string? Prerelease) : IComparable<SemanticVersion>
    {
        public static SemanticVersion Parse(string value)
        {
            if (string.IsNullOrWhiteSpace(value) || value.Length > 64 || value.StartsWith('v') || value.Contains('-') || value.Contains('+'))
                throw new InvalidDataException("Application version must be a stable X.Y.Z version.");
            var split = new[] { value };
            var numbers = value.Split('.');
            if (numbers.Length != 3 || numbers.Any(x => x.Length == 0 || x.Length > 1 && x[0] == '0') ||
                !int.TryParse(numbers[0], out var major) || !int.TryParse(numbers[1], out var minor) ||
                !int.TryParse(numbers[2], out var patch) || major < 0 || minor < 0 || patch < 0)
                throw new InvalidDataException("Application version is not canonical SemVer.");
            return new(major, minor, patch, null);
        }
        public int CompareTo(SemanticVersion other)
        {
            int result = Major.CompareTo(other.Major); if (result != 0) return result;
            result = Minor.CompareTo(other.Minor); if (result != 0) return result;
            result = Patch.CompareTo(other.Patch); if (result != 0) return result;
            if (Prerelease is null) return other.Prerelease is null ? 0 : 1;
            if (other.Prerelease is null) return -1;
            return string.CompareOrdinal(Prerelease, other.Prerelease);
        }
        public static bool operator >(SemanticVersion left, SemanticVersion right) => left.CompareTo(right) > 0;
        public static bool operator <(SemanticVersion left, SemanticVersion right) => left.CompareTo(right) < 0;
    }
}
