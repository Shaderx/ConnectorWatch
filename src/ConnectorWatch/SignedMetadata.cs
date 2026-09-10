using System.Globalization;
using System.Text.Json;

namespace ConnectorWatch;

/// <summary>A pinned ECDSA public key distributed in an authenticated application build.</summary>
public sealed record SignedMetadataKey
{
    public SignedMetadataKey(string keyId, string subjectPublicKeyInfoBase64, bool revoked = false)
    {
        KeyId = RequireToken(keyId, nameof(keyId), 64);
        SubjectPublicKeyInfoBase64 = RequireToken(subjectPublicKeyInfoBase64,
            nameof(subjectPublicKeyInfoBase64), 1024);
        Revoked = revoked;
    }

    public string KeyId { get; }
    public string SubjectPublicKeyInfoBase64 { get; }
    public bool Revoked { get; }

    private static string RequireToken(string value, string name, int maximumLength)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > maximumLength || value != value.Trim())
            throw new ArgumentException($"{name} is missing or malformed.", name);
        return value;
    }
}

/// <summary>
/// Authenticated bytes returned by <see cref="SignedMetadataVerifier"/>. The payload is not
/// interpreted until after its signature and SHA-256 digest have been checked.
/// </summary>
public sealed class VerifiedSignedMetadata
{
    private readonly byte[] _payloadBytes;

    internal VerifiedSignedMetadata(byte[] payloadBytes, string payloadSha256, string keyId)
    {
        _payloadBytes = payloadBytes.ToArray();
        PayloadSha256 = payloadSha256;
        KeyId = keyId;
    }

    public ReadOnlyMemory<byte> PayloadBytes => _payloadBytes.ToArray();
    public string PayloadSha256 { get; }
    public string KeyId { get; }
}

/// <summary>Common signed fields used by catalogs and application release metadata.</summary>
public sealed record SignedMetadataHeader(
    string PayloadType,
    int SchemaVersion,
    long Revision,
    DateTimeOffset IssuedUtc,
    DateTimeOffset ExpiresUtc)
{
    public static SignedMetadataHeader Parse(VerifiedSignedMetadata verified,
        string expectedPayloadType, string revisionPropertyName = "revision")
    {
        ArgumentNullException.ThrowIfNull(verified);
        if (string.IsNullOrWhiteSpace(expectedPayloadType))
            throw new ArgumentException("An expected payload type is required.", nameof(expectedPayloadType));
        if (string.IsNullOrWhiteSpace(revisionPropertyName))
            throw new ArgumentException("A revision property name is required.", nameof(revisionPropertyName));

        using var document = StrictJson.ParseObject(verified.PayloadBytes.Span, "signed payload");
        var root = document.RootElement;
        var payloadType = StrictJson.RequiredString(root, "payload_type", 80);
        if (!string.Equals(payloadType, expectedPayloadType, StringComparison.Ordinal))
            throw new InvalidDataException($"Signed payload type '{payloadType}' was not '{expectedPayloadType}'.");
        var schemaVersion = StrictJson.RequiredInt32(root, "schema_version");
        var revision = StrictJson.RequiredInt64(root, revisionPropertyName);
        var issuedUtc = StrictJson.RequiredUtcTimestamp(root, "issued_utc");
        var expiresUtc = StrictJson.RequiredUtcTimestamp(root, "expires_utc");
        return new SignedMetadataHeader(payloadType, schemaVersion, revision, issuedUtc, expiresUtc);
    }
}

public sealed record SignedMetadataWatermark(long Revision, string PayloadSha256);

/// <summary>Shared freeze, rollback, and same-revision equivocation checks.</summary>
public static class SignedMetadataPolicy
{
    private static readonly TimeSpan MaximumClockSkew = TimeSpan.FromMinutes(5);

    public static void Validate(SignedMetadataHeader header, VerifiedSignedMetadata verified,
        SignedMetadataWatermark? watermark, DateTimeOffset now, bool allowExpired = false)
    {
        ArgumentNullException.ThrowIfNull(header);
        ArgumentNullException.ThrowIfNull(verified);
        if (header.SchemaVersion != 1)
            throw new InvalidDataException($"Unsupported signed payload schema {header.SchemaVersion}.");
        if (header.Revision < 1)
            throw new InvalidDataException("Signed metadata revision must be positive.");
        if (header.ExpiresUtc <= header.IssuedUtc)
            throw new InvalidDataException("Signed metadata expiry must be after its issue time.");
        if (header.IssuedUtc > now + MaximumClockSkew)
            throw new InvalidDataException("Signed metadata issue time is in the future.");
        if (!allowExpired && header.ExpiresUtc <= now)
            throw new InvalidDataException("Signed metadata has expired.");
        if (watermark is null)
            return;
        ValidateDigest(watermark.PayloadSha256);
        if (header.Revision < watermark.Revision)
            throw new InvalidDataException(
                $"Signed metadata revision {header.Revision} is below accepted revision {watermark.Revision}.");
        if (header.Revision == watermark.Revision &&
            !string.Equals(verified.PayloadSha256, watermark.PayloadSha256, StringComparison.Ordinal))
            throw new InvalidDataException("A different payload reused an accepted signed metadata revision.");
    }

    public static SignedMetadataWatermark NextWatermark(SignedMetadataHeader header,
        VerifiedSignedMetadata verified, SignedMetadataWatermark? current = null)
    {
        ArgumentNullException.ThrowIfNull(header);
        ArgumentNullException.ThrowIfNull(verified);
        if (current is not null && header.Revision < current.Revision)
            return current;
        return new SignedMetadataWatermark(header.Revision, verified.PayloadSha256);
    }

    internal static void ValidateDigest(string value)
    {
        if (value.Length != 64 || value.Any(c => !char.IsAsciiHexDigit(c) || char.IsUpper(c)))
            throw new InvalidDataException("SHA-256 digest must be 64 lowercase hexadecimal characters.");
    }
}

internal static class StrictJson
{
    public static JsonDocument ParseObject(ReadOnlySpan<byte> utf8, string label)
    {
        try
        {
            var document = JsonDocument.Parse(utf8.ToArray(), new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 32,
            });
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                document.Dispose();
                throw new InvalidDataException($"The {label} must be a JSON object.");
            }
            EnsureNoDuplicateProperties(document.RootElement, label);
            return document;
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"The {label} is not valid JSON.", ex);
        }
    }

    public static void RequireOnlyProperties(JsonElement element, string label, params string[] allowed)
    {
        var allowedSet = new HashSet<string>(allowed, StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
            if (!allowedSet.Contains(property.Name))
                throw new InvalidDataException($"Unknown {label} property '{property.Name}'.");
        foreach (var required in allowed)
            if (!element.TryGetProperty(required, out _))
                throw new InvalidDataException($"Required {label} property '{required}' is missing.");
    }

    public static string RequiredString(JsonElement element, string name, int maximumLength)
    {
        if (!element.TryGetProperty(name, out var property) || property.ValueKind != JsonValueKind.String)
            throw new InvalidDataException($"Required string property '{name}' is missing.");
        var value = property.GetString()!;
        if (string.IsNullOrWhiteSpace(value) || value.Length > maximumLength || value != value.Trim())
            throw new InvalidDataException($"Property '{name}' is empty or malformed.");
        return value;
    }

    public static int RequiredInt32(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var property) || !property.TryGetInt32(out var value))
            throw new InvalidDataException($"Required integer property '{name}' is missing or malformed.");
        return value;
    }

    public static long RequiredInt64(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var property) || !property.TryGetInt64(out var value))
            throw new InvalidDataException($"Required integer property '{name}' is missing or malformed.");
        return value;
    }

    public static DateTimeOffset RequiredUtcTimestamp(JsonElement element, string name)
    {
        var text = RequiredString(element, name, 40);
        if (!DateTimeOffset.TryParseExact(text, "yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var value))
            throw new InvalidDataException($"Property '{name}' must be a whole-second UTC timestamp.");
        return value;
    }

    private static void EnsureNoDuplicateProperties(JsonElement element, string path)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                var names = new HashSet<string>(StringComparer.Ordinal);
                foreach (var property in element.EnumerateObject())
                {
                    if (!names.Add(property.Name))
                        throw new InvalidDataException($"Duplicate JSON property '{property.Name}' in {path}.");
                    EnsureNoDuplicateProperties(property.Value, path + "." + property.Name);
                }
                break;
            case JsonValueKind.Array:
                var index = 0;
                foreach (var item in element.EnumerateArray())
                    EnsureNoDuplicateProperties(item, $"{path}[{index++}]");
                break;
        }
    }
}
