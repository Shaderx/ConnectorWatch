using System.Text.Json;

namespace ConnectorWatch;

public sealed record DriverIdentity
{
    public DriverIdentity(string vendorId, string deviceId, string subsystemId,
        string os, string architecture, string driverVersion)
    {
        VendorId = NormalizeHex(vendorId, 4, nameof(vendorId));
        DeviceId = NormalizeHex(deviceId, 4, nameof(deviceId));
        SubsystemId = NormalizeHex(subsystemId, 8, nameof(subsystemId));
        Os = NormalizeName(os, nameof(os));
        Architecture = NormalizeName(architecture, nameof(architecture));
        DriverVersion = RequireText(driverVersion, nameof(driverVersion), 40);
    }

    public string VendorId { get; }
    public string DeviceId { get; }
    public string SubsystemId { get; }
    public string Os { get; }
    public string Architecture { get; }
    public string DriverVersion { get; }

    private static string NormalizeHex(string value, int length, string name)
    {
        var normalized = RequireText(value, name, length).ToUpperInvariant();
        if (normalized.Length != length || normalized.Any(c => !char.IsAsciiHexDigit(c)))
            throw new ArgumentException($"{name} must be exactly {length} hexadecimal characters.", name);
        return normalized;
    }

    private static string NormalizeName(string value, string name) =>
        RequireText(value, name, 40).ToLowerInvariant();

    private static string RequireText(string value, string name, int maximumLength)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException($"{name} is required.", name);
        var trimmed = value.Trim();
        if (trimmed.Length > maximumLength)
            throw new ArgumentException($"{name} exceeds {maximumLength} characters.", name);
        return trimmed;
    }
}

public enum DriverApprovalState
{
    Approved,
    NotListed,
    Revoked,
    RequiresNewerReader,
    RequiresNewerApp,
    ApprovalUnavailable,
}

public sealed class DriverApprovalException : InvalidOperationException
{
    public DriverApprovalException(string message) : base(message) { }
}

/// <summary>An immutable capability bound to one exact observed native-reader scope.</summary>
public sealed class DriverApprovalDecision
{
    private readonly DriverIdentity _identity;
    private readonly string _profile;
    private readonly string _readerVersion;
    private readonly string _appVersion;

    internal DriverApprovalDecision(DriverApprovalState state, bool mayStart, string reason,
        long catalogRevision, DateTimeOffset? expires, bool isUnvalidated,
        DriverIdentity identity, string profile, string readerVersion, string appVersion)
    {
        State = state;
        MayStart = mayStart;
        Reason = reason;
        CatalogRevision = catalogRevision;
        Expires = expires;
        IsUnvalidated = isUnvalidated;
        _identity = identity;
        _profile = profile;
        _readerVersion = readerVersion;
        _appVersion = appVersion;
    }

    public DriverApprovalState State { get; }
    public bool MayStart { get; }
    public string Reason { get; }
    public long CatalogRevision { get; }
    public DateTimeOffset? Expires { get; }
    public bool IsUnvalidated { get; }

    public void RequireApplicable(DriverIdentity identity, string profile, string readerVersion,
        string appVersion, DateTimeOffset? now = null)
    {
        ArgumentNullException.ThrowIfNull(identity);
        if (!_identity.Equals(identity) || !string.Equals(_profile, profile, StringComparison.Ordinal) ||
            !string.Equals(_readerVersion, readerVersion, StringComparison.Ordinal) ||
            !string.Equals(_appVersion, appVersion, StringComparison.Ordinal))
            throw new DriverApprovalException("The driver approval decision does not match the observed native-reader scope.");
        if (!MayStart)
            throw new DriverApprovalException(Reason);
        if (!IsUnvalidated && Expires is not null && Expires <= (now ?? DateTimeOffset.UtcNow))
            throw new DriverApprovalException("The driver approval catalog has expired; refresh approvals before private rail acquisition.");
    }
}

public sealed record DriverCatalogEntry(
    DriverIdentity Identity,
    string ReaderProfile,
    string MinimumReaderVersion,
    string MaximumReaderVersionExclusive,
    string MinimumAppVersion,
    string MaximumAppVersionExclusive,
    string Decision,
    string EvidenceSha256,
    DateTimeOffset DecisionUtc,
    string Rationale)
{
    internal bool MatchesIdentity(DriverIdentity identity, string profile) =>
        Identity.Equals(identity) && string.Equals(ReaderProfile, profile, StringComparison.Ordinal);

    internal bool SupportsReader(string version) => VersionRange.Contains(
        version, MinimumReaderVersion, MaximumReaderVersionExclusive);

    internal bool ReaderIsOlderThanMinimum(string version) =>
        VersionRange.IsBelow(version, MinimumReaderVersion);

    internal bool SupportsApp(string version) => VersionRange.Contains(
        version, MinimumAppVersion, MaximumAppVersionExclusive);

    internal bool AppIsOlderThanMinimum(string version) =>
        VersionRange.IsBelow(version, MinimumAppVersion);
}

public sealed class DriverCatalog
{
    internal DriverCatalog(SignedMetadataHeader header, IReadOnlyList<DriverCatalogEntry> entries)
    {
        Header = header;
        Entries = entries;
    }

    public SignedMetadataHeader Header { get; }
    public long CatalogRevision => Header.Revision;
    public DateTimeOffset IssuedUtc => Header.IssuedUtc;
    public DateTimeOffset ExpiresUtc => Header.ExpiresUtc;
    public IReadOnlyList<DriverCatalogEntry> Entries { get; }
}

/// <summary>Strict parser shared by the runtime cache and protected catalog publication.</summary>
public static class DriverCatalogPayload
{
    public const string PayloadType = "connectorwatch-driver-catalog";
    public const int SchemaVersion = 1;

    public static DriverCatalog ParseVerified(VerifiedSignedMetadata verified,
        IReadOnlySet<string>? supportedProfiles = null)
    {
        ArgumentNullException.ThrowIfNull(verified);
        return Parse(verified.PayloadBytes.Span, supportedProfiles);
    }

    public static DriverCatalog Parse(ReadOnlySpan<byte> payloadBytes,
        IReadOnlySet<string>? supportedProfiles = null) => ParseCore(payloadBytes, supportedProfiles);

    public static DriverCatalog ParseVerifiedPayload(VerifiedSignedMetadata verified,
        IReadOnlySet<string>? supportedProfiles = null) => ParseVerified(verified, supportedProfiles);

    private static DriverCatalog ParseCore(ReadOnlySpan<byte> payloadBytes,
        IReadOnlySet<string>? supportedProfiles)
    {
        if (payloadBytes.IsEmpty || payloadBytes.Length > SignedMetadataVerifier.MaximumPayloadBytes)
            throw new InvalidDataException("Driver catalog payload is empty or oversized.");
        using var document = StrictJson.ParseObject(payloadBytes, "driver catalog payload");
        var root = document.RootElement;
        StrictJson.RequireOnlyProperties(root, "driver catalog payload", "payload_type", "schema_version",
            "catalog_revision", "issued_utc", "expires_utc", "entries");
        var payloadType = StrictJson.RequiredString(root, "payload_type", 80);
        if (!string.Equals(payloadType, PayloadType, StringComparison.Ordinal))
            throw new InvalidDataException($"Unexpected driver catalog payload type '{payloadType}'.");
        var schema = StrictJson.RequiredInt32(root, "schema_version");
        if (schema != SchemaVersion)
            throw new InvalidDataException($"Unsupported driver catalog schema {schema}.");
        var revision = StrictJson.RequiredInt64(root, "catalog_revision");
        if (revision < 1) throw new InvalidDataException("Catalog revision must be positive.");
        var issued = StrictJson.RequiredUtcTimestamp(root, "issued_utc");
        var expires = StrictJson.RequiredUtcTimestamp(root, "expires_utc");
        if (expires <= issued) throw new InvalidDataException("Catalog expiry must follow its issue time.");
        if (expires - issued > TimeSpan.FromDays(31))
            throw new InvalidDataException("Driver catalog validity cannot exceed 31 days.");
        if (!root.TryGetProperty("entries", out var entriesElement) || entriesElement.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("Driver catalog entries must be an array.");
        if (entriesElement.GetArrayLength() > 4096)
            throw new InvalidDataException("Driver catalog has too many entries.");

        var entries = new List<DriverCatalogEntry>(entriesElement.GetArrayLength());
        var scopes = new HashSet<string>(StringComparer.Ordinal);
        var index = 0;
        foreach (var element in entriesElement.EnumerateArray())
        {
            if (element.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException($"Driver catalog entry {index} must be an object.");
            StrictJson.RequireOnlyProperties(element, $"driver catalog entry {index}",
                "vendor_id", "device_id", "subsystem_id", "os", "architecture", "driver_version",
                "reader_profile", "minimum_reader_version", "maximum_reader_version_exclusive",
                "minimum_app_version", "maximum_app_version_exclusive", "decision", "evidence_sha256",
                "decision_utc", "rationale");
            var vendor = StrictJson.RequiredString(element, "vendor_id", 4);
            var device = StrictJson.RequiredString(element, "device_id", 4);
            var subsystem = StrictJson.RequiredString(element, "subsystem_id", 8);
            var os = StrictJson.RequiredString(element, "os", 40);
            var architecture = StrictJson.RequiredString(element, "architecture", 40);
            var driver = StrictJson.RequiredString(element, "driver_version", 40);
            DriverIdentity identity;
            try { identity = new DriverIdentity(vendor, device, subsystem, os, architecture, driver); }
            catch (ArgumentException ex)
            {
                throw new InvalidDataException($"Driver catalog entry {index} identity is malformed.", ex);
            }
            if (vendor != identity.VendorId || device != identity.DeviceId || subsystem != identity.SubsystemId ||
                os != identity.Os || architecture != identity.Architecture || driver != identity.DriverVersion)
                throw new InvalidDataException($"Driver catalog entry {index} identity is not canonical.");
            var profile = StrictJson.RequiredString(element, "reader_profile", 80);
            if (supportedProfiles is not null && !supportedProfiles.Contains(profile))
                throw new InvalidDataException($"Driver catalog entry {index} names unsupported profile '{profile}'.");
            var minimumReader = StrictJson.RequiredString(element, "minimum_reader_version", 32);
            var maximumReader = StrictJson.RequiredString(element, "maximum_reader_version_exclusive", 32);
            var minimumApp = StrictJson.RequiredString(element, "minimum_app_version", 32);
            var maximumApp = StrictJson.RequiredString(element, "maximum_app_version_exclusive", 32);
            VersionRange.Validate(minimumReader, maximumReader, $"entry {index} reader");
            VersionRange.Validate(minimumApp, maximumApp, $"entry {index} app");
            var decision = StrictJson.RequiredString(element, "decision", 16);
            if (decision is not ("approved" or "revoked"))
                throw new InvalidDataException($"Driver catalog entry {index} has invalid decision '{decision}'.");
            var evidence = StrictJson.RequiredString(element, "evidence_sha256", 64);
            SignedMetadataPolicy.ValidateDigest(evidence);
            var decisionUtc = StrictJson.RequiredUtcTimestamp(element, "decision_utc");
            if (decisionUtc > issued)
                throw new InvalidDataException($"Driver catalog entry {index} decision is newer than the catalog.");
            var rationale = StrictJson.RequiredString(element, "rationale", 500);
            var scope = string.Join('|', identity.VendorId, identity.DeviceId, identity.SubsystemId,
                identity.Os, identity.Architecture, identity.DriverVersion, profile);
            if (!scopes.Add(scope))
                throw new InvalidDataException($"Driver catalog entry {index} duplicates an exact approval scope.");
            entries.Add(new DriverCatalogEntry(identity, profile, minimumReader, maximumReader,
                minimumApp, maximumApp, decision, evidence, decisionUtc, rationale));
            index++;
        }
        var header = new SignedMetadataHeader(payloadType, schema, revision, issued, expires);
        return new DriverCatalog(header, entries.AsReadOnly());
    }
}

internal static class VersionRange
{
    public static bool Contains(string value, string minimum, string maximumExclusive)
    {
        var parsed = Parse(value, nameof(value));
        return parsed.CompareTo(Parse(minimum, nameof(minimum))) >= 0 &&
            parsed.CompareTo(Parse(maximumExclusive, nameof(maximumExclusive))) < 0;
    }

    public static bool IsBelow(string value, string minimum) =>
        Parse(value, nameof(value)).CompareTo(Parse(minimum, nameof(minimum))) < 0;

    public static void Validate(string minimum, string maximumExclusive, string label)
    {
        if (Parse(minimum, label).CompareTo(Parse(maximumExclusive, label)) >= 0)
            throw new InvalidDataException($"The {label} version range is empty or reversed.");
    }

    private static Version Parse(string value, string label)
    {
        var parts = value.Split('.');
        if (parts.Length is < 2 or > 4 || parts.Any(part => part.Length == 0 ||
                part.Any(c => !char.IsAsciiDigit(c)) || (part.Length > 1 && part[0] == '0')))
            throw new InvalidDataException($"The {label} version '{value}' is not a canonical numeric version.");
        var numbers = parts.Select(part => int.TryParse(part, out var number) ? number : -1).ToArray();
        if (numbers.Any(number => number < 0))
            throw new InvalidDataException($"The {label} version '{value}' is out of range.");
        return new Version(numbers[0], numbers[1], parts.Length > 2 ? numbers[2] : 0,
            parts.Length > 3 ? numbers[3] : 0);
    }
}
