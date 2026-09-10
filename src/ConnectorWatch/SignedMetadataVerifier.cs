using System.Security.Cryptography;
using System.Text.Json;

namespace ConnectorWatch;

/// <summary>
/// Verifies ConnectorWatch's narrow signed-metadata envelope. This protocol is not TUF.
/// Trust keys can only arrive in an authenticated application build.
/// </summary>
public sealed class SignedMetadataVerifier
{
    public const string Algorithm = "ecdsa-p256-sha256";
    public const int MaximumEnvelopeBytes = 384 * 1024;
    public const int MaximumPayloadBytes = 256 * 1024;
    private const int P1363SignatureBytes = 64;
    private readonly IReadOnlyDictionary<string, SignedMetadataKey> _keys;

    public SignedMetadataVerifier(IEnumerable<SignedMetadataKey> keys)
    {
        ArgumentNullException.ThrowIfNull(keys);
        var dictionary = new Dictionary<string, SignedMetadataKey>(StringComparer.Ordinal);
        foreach (var key in keys)
            if (!dictionary.TryAdd(key.KeyId, key))
                throw new ArgumentException($"Duplicate signed metadata key id '{key.KeyId}'.", nameof(keys));
        _keys = dictionary;
    }

    public VerifiedSignedMetadata VerifyEnvelope(ReadOnlySpan<byte> envelopeBytes)
    {
        if (envelopeBytes.IsEmpty || envelopeBytes.Length > MaximumEnvelopeBytes)
            throw new InvalidDataException(
                $"Signed metadata envelope must be between 1 and {MaximumEnvelopeBytes} bytes.");

        using var document = StrictJson.ParseObject(envelopeBytes, "signed metadata envelope");
        var root = document.RootElement;
        StrictJson.RequireOnlyProperties(root, "signed metadata envelope",
            "envelope_schema", "key_id", "algorithm", "payload_sha256", "payload", "signature");
        if (StrictJson.RequiredInt32(root, "envelope_schema") != 1)
            throw new InvalidDataException("Unsupported signed metadata envelope schema.");
        var keyId = StrictJson.RequiredString(root, "key_id", 64);
        if (!_keys.TryGetValue(keyId, out var key))
            throw new InvalidDataException($"Signed metadata key '{keyId}' is not trusted.");
        if (key.Revoked)
            throw new InvalidDataException($"Signed metadata key '{keyId}' is revoked.");
        var algorithm = StrictJson.RequiredString(root, "algorithm", 64);
        if (!string.Equals(algorithm, Algorithm, StringComparison.Ordinal))
            throw new InvalidDataException($"Unsupported signed metadata algorithm '{algorithm}'.");
        var declaredDigest = StrictJson.RequiredString(root, "payload_sha256", 64);
        SignedMetadataPolicy.ValidateDigest(declaredDigest);

        var payload = DecodeBase64(root, "payload", MaximumPayloadBytes);
        var signature = DecodeBase64(root, "signature", P1363SignatureBytes);
        if (signature.Length != P1363SignatureBytes)
            throw new InvalidDataException("ECDSA P-256 signature must be a 64-byte IEEE-P1363 value.");
        var digest = SHA256.HashData(payload);
        byte[] declaredDigestBytes;
        try { declaredDigestBytes = Convert.FromHexString(declaredDigest); }
        catch (FormatException ex) { throw new InvalidDataException("Signed metadata payload digest is malformed.", ex); }
        if (!CryptographicOperations.FixedTimeEquals(digest, declaredDigestBytes))
            throw new InvalidDataException("Signed metadata payload SHA-256 does not match its envelope.");

        byte[] publicKey;
        try { publicKey = Convert.FromBase64String(key.SubjectPublicKeyInfoBase64); }
        catch (FormatException ex) { throw new InvalidDataException($"Trusted key '{keyId}' is malformed.", ex); }
        try
        {
            using var ecdsa = ECDsa.Create();
            ecdsa.ImportSubjectPublicKeyInfo(publicKey, out var bytesRead);
            if (bytesRead != publicKey.Length)
                throw new InvalidDataException($"Trusted key '{keyId}' has trailing data.");
            var parameters = ecdsa.ExportParameters(false);
            if (parameters.Curve.Oid.Value != "1.2.840.10045.3.1.7" ||
                parameters.Q.X?.Length != 32 || parameters.Q.Y?.Length != 32)
                throw new InvalidDataException($"Trusted key '{keyId}' is not ECDSA P-256.");
            if (!ecdsa.VerifyHash(digest, signature, DSASignatureFormat.IeeeP1363FixedFieldConcatenation))
                throw new InvalidDataException("Signed metadata signature is invalid.");
        }
        catch (CryptographicException ex)
        {
            throw new InvalidDataException($"Trusted key '{keyId}' or its signature is invalid.", ex);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(publicKey);
        }
        return new VerifiedSignedMetadata(payload, declaredDigest, keyId);
    }

    private static byte[] DecodeBase64(JsonElement root, string name, int maximumDecodedBytes)
    {
        var encoded = StrictJson.RequiredString(root, name, maximumDecodedBytes * 2);
        byte[] value;
        try { value = Convert.FromBase64String(encoded); }
        catch (FormatException ex) { throw new InvalidDataException($"Property '{name}' is not valid base64.", ex); }
        if (value.Length > maximumDecodedBytes)
            throw new InvalidDataException($"Property '{name}' exceeds the {maximumDecodedBytes}-byte limit.");
        return value;
    }
}
