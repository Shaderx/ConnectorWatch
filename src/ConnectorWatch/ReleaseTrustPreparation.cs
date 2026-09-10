using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ConnectorWatch;

/// <summary>Build-time validation of public trust material; this command never signs or approves drivers.</summary>
public static class ReleaseTrustPreparation
{
    public static bool TryRun(string[] args, out int exitCode)
    {
        exitCode = 0;
        if (!args.Contains("--prepare-release-trust", StringComparer.Ordinal)) return false;
        Prepare(Required(args, "--catalog-trust"), Required(args, "--initial-catalog"),
            Required(args, "--app-trust"), Required(args, "--output"), DateTimeOffset.UtcNow);
        return true;
    }

    internal static void Prepare(string catalogTrustPath, string initialCatalogPath,
        string appTrustPath, string outputDirectory, DateTimeOffset now)
    {
        byte[] catalogTrustBytes = ReadBounded(catalogTrustPath, 32 * 1024);
        using var catalogTrust = StrictJson.ParseObject(catalogTrustBytes, "catalog trust");
        StrictJson.RequireOnlyProperties(catalogTrust.RootElement, "catalog trust", "schema_version", "keys");
        if (StrictJson.RequiredInt32(catalogTrust.RootElement, "schema_version") != 1)
            throw new InvalidDataException("Unknown catalog trust schema.");
        var keys = ReadKeys(catalogTrust.RootElement.GetProperty("keys"));
        if (!keys.Any(k => !k.Revoked)) throw new InvalidDataException("An active catalog signing key is required.");
        byte[] envelope = ReadBounded(initialCatalogPath, SignedMetadataVerifier.MaximumEnvelopeBytes);
        var verified = new SignedMetadataVerifier(keys).VerifyEnvelope(envelope);
        var catalog = DriverCatalogPayload.ParseVerified(verified,
            new HashSet<string>(StringComparer.Ordinal) { DirectNvRails.ReaderProfile });
        SignedMetadataPolicy.Validate(catalog.Header, verified, null, now);

        byte[] appTrustBytes = ReadBounded(appTrustPath, 32 * 1024);
        using var appTrust = StrictJson.ParseObject(appTrustBytes, "app update trust");
        var appProperties = new List<string> { "schema_version", "metadata_url", "metadata_keys", "publisher_certificate_sha256" };
        if (appTrust.RootElement.TryGetProperty("self_signed_publisher_certificate_sha256", out _))
            appProperties.Add("self_signed_publisher_certificate_sha256");
        StrictJson.RequireOnlyProperties(appTrust.RootElement, "app update trust", appProperties.ToArray());
        if (StrictJson.RequiredInt32(appTrust.RootElement, "schema_version") != 1)
            throw new InvalidDataException("Unknown app trust schema.");
        var appKeys = ReadKeys(appTrust.RootElement.GetProperty("metadata_keys"));
        if (!appKeys.Any(k => !k.Revoked)) throw new InvalidDataException("An active application signing key is required.");
        var allCatalogMaterial = keys.Select(k => k.SubjectPublicKeyInfoBase64).ToHashSet(StringComparer.Ordinal);
        if (appKeys.Any(k => allCatalogMaterial.Contains(k.SubjectPublicKeyInfoBase64)))
            throw new InvalidDataException("Catalog and application metadata must use separate signing keys.");
        string url = StrictJson.RequiredString(appTrust.RootElement, "metadata_url", 2048);
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps ||
            !string.IsNullOrEmpty(uri.UserInfo) || uri.AbsolutePath.Contains("driver-catalog", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Application metadata needs a separate HTTPS discovery endpoint.");
        var publisherPins = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        ReadPublisherPins(appTrust.RootElement.GetProperty("publisher_certificate_sha256"), publisherPins);
        if (appTrust.RootElement.TryGetProperty("self_signed_publisher_certificate_sha256", out var selfSignedPins))
            ReadPublisherPins(selfSignedPins, publisherPins);
        if (publisherPins.Count == 0)
            throw new InvalidDataException("At least one explicit public-trusted or self-signed publisher pin is required.");

        string output = Path.GetFullPath(outputDirectory);
        if (Directory.Exists(output) && Directory.EnumerateFileSystemEntries(output).Any())
            throw new IOException("Release trust output must be empty.");
        Directory.CreateDirectory(Path.Combine(output, "trust"));
        var source = new StringBuilder("namespace ConnectorWatch;\ninternal static partial class DriverCatalogReleaseTrust\n{\n    static partial void AddReleaseKeys(System.Collections.Generic.List<SignedMetadataKey> keys)\n    {\n");
        foreach (var key in keys)
            source.Append("        keys.Add(new SignedMetadataKey(").Append(JsonSerializer.Serialize(key.KeyId)).Append(", ")
                .Append(JsonSerializer.Serialize(key.SubjectPublicKeyInfoBase64)).Append(", ")
                .Append(key.Revoked ? "true" : "false").Append("));\n");
        source.Append("    }\n}\n");
        File.WriteAllText(Path.Combine(output, "DriverCatalogReleaseTrust.Generated.cs"), source.ToString(), new UTF8Encoding(false));
        File.WriteAllBytes(Path.Combine(output, "app-update-trust.json"), appTrustBytes);
        File.WriteAllBytes(Path.Combine(output, "driver-catalog-trust.json"), catalogTrustBytes);
        File.WriteAllBytes(Path.Combine(output, "trust", "driver-catalog.signed.json"), envelope);
        File.WriteAllBytes(Path.Combine(output, "release-trust-manifest.json"), JsonSerializer.SerializeToUtf8Bytes(new
        {
            schema_version = 1, catalog_revision = catalog.CatalogRevision,
            catalog_payload_sha256 = verified.PayloadSha256,
            app_trust_sha256 = Convert.ToHexString(SHA256.HashData(appTrustBytes)).ToLowerInvariant(),
        }));
    }

    private static void ReadPublisherPins(JsonElement pins, HashSet<string> seen)
    {
        if (pins.ValueKind != JsonValueKind.Array || pins.GetArrayLength() > 16)
            throw new InvalidDataException("Publisher pins must be a bounded array.");
        foreach (var pin in pins.EnumerateArray())
        {
            if (pin.ValueKind != JsonValueKind.String || pin.GetString() is not string value ||
                value.Length != 64 || value.Any(c => !char.IsAsciiHexDigit(c)))
                throw new InvalidDataException("Publisher pins must be SHA-256 certificate fingerprints.");
            if (!seen.Add(value))
                throw new InvalidDataException("Duplicate publisher pin; each certificate must have exactly one trust policy.");
        }
    }

    private static List<SignedMetadataKey> ReadKeys(JsonElement array)
    {
        if (array.ValueKind != JsonValueKind.Array || array.GetArrayLength() is < 1 or > 16)
            throw new InvalidDataException("Trust keys must be a bounded nonempty array.");
        var keys = new List<SignedMetadataKey>();
        foreach (var value in array.EnumerateArray())
        {
            if (value.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Invalid public key.");
            StrictJson.RequireOnlyProperties(value, "public key", "key_id", "subject_public_key_info_base64", "revoked");
            var id = StrictJson.RequiredString(value, "key_id", 64);
            var material = StrictJson.RequiredString(value, "subject_public_key_info_base64", 1024);
            var revoked = value.GetProperty("revoked");
            if (revoked.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) throw new InvalidDataException("Invalid key revocation flag.");
            using var ec = ECDsa.Create();
            var bytes = Convert.FromBase64String(material);
            ec.ImportSubjectPublicKeyInfo(bytes, out int used);
            if (used != bytes.Length || Convert.ToBase64String(ec.ExportSubjectPublicKeyInfo()) != material ||
                ec.ExportParameters(false).Curve.Oid.Value != "1.2.840.10045.3.1.7")
                throw new InvalidDataException("Public keys must be canonical ECDSA P-256 SPKI.");
            if (keys.Any(k => k.KeyId == id || k.SubjectPublicKeyInfoBase64 == material))
                throw new InvalidDataException("Duplicate public key or key identifier.");
            keys.Add(new(id, material, revoked.GetBoolean()));
        }
        return keys;
    }

    private static byte[] ReadBounded(string path, int maximum)
    {
        using var stream = File.OpenRead(path);
        if (stream.Length > maximum) throw new InvalidDataException("Release trust input is oversized.");
        var bytes = new byte[checked((int)stream.Length)]; stream.ReadExactly(bytes); return bytes;
    }
    private static string Required(string[] args, string flag)
    {
        int index = Array.IndexOf(args, flag);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : throw new ArgumentException("Missing " + flag);
    }
}
