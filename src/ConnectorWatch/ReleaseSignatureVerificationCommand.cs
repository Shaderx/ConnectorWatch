using System.Text.Json;

namespace ConnectorWatch;

/// <summary>Machine-facing release signature check used by the publication workflow.</summary>
public static class ReleaseSignatureVerificationCommand
{
    public static bool TryHandle(string[] args, out int exitCode)
    {
        if (!args.Contains("--verify-release-signature", StringComparer.Ordinal))
        {
            exitCode = 0;
            return false;
        }

        try
        {
            var file = RequiredSingleOption(args, "--file");
            var appTrust = RequiredSingleOption(args, "--app-trust");
            var trust = AppUpdateTrustConfigurationReader.ReadFile(appTrust);
            if (trust.SchemaVersion != 1)
                throw new InvalidDataException("Application update trust configuration schema is unsupported.");
            var pins = WindowsAuthenticodeVerifier.NormalizeConfiguredPins(
                trust.PublisherCertificateSha256, trust.SelfSignedPublisherCertificateSha256);
            var fullPath = Path.GetFullPath(file);
            new WindowsAuthenticodeVerifier().Verify(fullPath, pins.Public, pins.SelfSigned);
            Console.WriteLine(JsonSerializer.Serialize(new { valid = true, file = fullPath }));
            exitCode = 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                valid = false,
                error = ex.GetType().Name,
                detail = ex.Message,
            }));
            exitCode = 2;
        }
        return true;
    }

    static string RequiredSingleOption(string[] args, string name)
    {
        var indexes = args.Select((value, index) => (value, index))
            .Where(item => string.Equals(item.value, name, StringComparison.Ordinal))
            .Select(item => item.index)
            .ToArray();
        if (indexes.Length != 1 || indexes[0] + 1 >= args.Length || args[indexes[0] + 1].StartsWith("--", StringComparison.Ordinal))
            throw new ArgumentException($"{name} must be supplied exactly once with a value.");
        return args[indexes[0] + 1];
    }
}
