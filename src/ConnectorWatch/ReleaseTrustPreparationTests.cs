using System.Security.Cryptography;
using System.Text.Json;

namespace ConnectorWatch;

public static class ReleaseTrustPreparationTests
{
    public static void Run()
    {
        var directory = Path.Combine(Path.GetTempPath(), "ConnectorWatch-trust-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            using var catalogKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            using var appKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            object Key(ECDsa ec, string id) => new
            {
                key_id = id, subject_public_key_info_base64 = Convert.ToBase64String(ec.ExportSubjectPublicKeyInfo()), revoked = false,
            };
            var catalogTrust = Path.Combine(directory, "catalog-trust.json");
            var appTrust = Path.Combine(directory, "app-trust.json");
            var envelopePath = Path.Combine(directory, "catalog.json");
            File.WriteAllBytes(catalogTrust, JsonSerializer.SerializeToUtf8Bytes(new { schema_version = 1, keys = new[] { Key(catalogKey, "catalog") } }));
            void WriteAppTrust(ECDsa key) => File.WriteAllBytes(appTrust, JsonSerializer.SerializeToUtf8Bytes(new
            {
                schema_version = 1, metadata_url = "https://example.com/app-stable/app-current.json",
                metadata_keys = new[] { Key(key, "app") }, publisher_certificate_sha256 = new[] { new string('a', 64) },
            }));
            WriteAppTrust(appKey);
            var now = DateTimeOffset.Parse("2026-09-10T00:00:00Z");
            var payload = JsonSerializer.SerializeToUtf8Bytes(new
            {
                payload_type = "connectorwatch-driver-catalog", schema_version = 1, catalog_revision = 1,
                issued_utc = "2026-09-10T00:00:00Z", expires_utc = "2026-10-10T00:00:00Z", entries = Array.Empty<object>(),
            });
            File.WriteAllBytes(envelopePath, JsonSerializer.SerializeToUtf8Bytes(new
            {
                envelope_schema = 1, key_id = "catalog", algorithm = "ecdsa-p256-sha256",
                payload_sha256 = Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant(),
                payload = Convert.ToBase64String(payload), signature = Convert.ToBase64String(catalogKey.SignData(payload,
                    HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation)),
            }));
            var output = Path.Combine(directory, "prepared");
            ReleaseTrustPreparation.Prepare(catalogTrust, envelopePath, appTrust, output, now);
            if (!File.Exists(Path.Combine(output, "DriverCatalogReleaseTrust.Generated.cs")) ||
                !File.ReadAllBytes(Path.Combine(output, "trust", "driver-catalog.signed.json")).SequenceEqual(File.ReadAllBytes(envelopePath)))
                throw new Exception("Prepared release did not preserve its verified signed catalog.");
            void Reject(Action action)
            {
                try { action(); } catch (InvalidDataException) { return; }
                throw new Exception("Unsafe release trust was accepted.");
            }
            Reject(() => ReleaseTrustPreparation.Prepare(catalogTrust, envelopePath, appTrust, Path.Combine(directory, "expired"), now.AddDays(31)));
            WriteAppTrust(catalogKey);
            Reject(() => ReleaseTrustPreparation.Prepare(catalogTrust, envelopePath, appTrust, Path.Combine(directory, "same-role-key"), now));
            if (Directory.Exists(Path.Combine(directory, "same-role-key"))) throw new Exception("Invalid release input wrote output.");
            void WritePublisherTrust(string[] publicPins, string[] selfSignedPins) => File.WriteAllBytes(appTrust,
                JsonSerializer.SerializeToUtf8Bytes(new
                {
                    schema_version = 1, metadata_url = "https://example.com/app-stable/app-current.json",
                    metadata_keys = new[] { Key(appKey, "app") }, publisher_certificate_sha256 = publicPins,
                    self_signed_publisher_certificate_sha256 = selfSignedPins,
                }));
            WritePublisherTrust([], [new string('b', 64)]);
            ReleaseTrustPreparation.Prepare(catalogTrust, envelopePath, appTrust, Path.Combine(directory, "self-signed"), now);
            WritePublisherTrust([new string('a', 64)], [new string('b', 64)]);
            ReleaseTrustPreparation.Prepare(catalogTrust, envelopePath, appTrust, Path.Combine(directory, "bridge"), now);
            WritePublisherTrust([], []);
            Reject(() => ReleaseTrustPreparation.Prepare(catalogTrust, envelopePath, appTrust, Path.Combine(directory, "no-pins"), now));
            WritePublisherTrust([new string('a', 64)], [new string('A', 64)]);
            Reject(() => ReleaseTrustPreparation.Prepare(catalogTrust, envelopePath, appTrust, Path.Combine(directory, "ambiguous-policy"), now));
            WritePublisherTrust([], ["not-a-fingerprint"]);
            Reject(() => ReleaseTrustPreparation.Prepare(catalogTrust, envelopePath, appTrust, Path.Combine(directory, "invalid-pin"), now));
            Console.WriteLine("PASS: release trust role separation, initial signature/expiry, self-signed and bridge policies, and staged outputs.");
        }
        finally { Directory.Delete(directory, true); }
    }
}
