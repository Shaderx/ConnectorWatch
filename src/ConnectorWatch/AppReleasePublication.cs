using System.Security.Cryptography;

namespace ConnectorWatch;

public static class AppReleasePublication
{
    public const string ValidationSwitch = "--validate-app-release-publication";

    public static bool TryHandle(string[] args, out int exitCode)
    {
        if (!args.Contains(ValidationSwitch, StringComparer.Ordinal)) { exitCode = 0; return false; }
        var envelopePath = Path.GetFullPath(Required(args, ValidationSwitch));
        var installerPath = Path.GetFullPath(Required(args, "--installer"));
        var keyId = Required(args, "--key-id");
        var publicKey = Required(args, "--public-key-spki-base64");
        var expectedVersion = Required(args, "--expected-version");
        var verifier = new SignedMetadataVerifier([new SignedMetadataKey(keyId, publicKey)]);
        var release = AppReleaseMetadataValidator.ValidateAuthenticatedEnvelope(
            File.ReadAllBytes(envelopePath), verifier, DateTimeOffset.UtcNow);
        var installer = new FileInfo(installerPath);
        if (!installer.Exists || installer.Length != release.InstallerSize || release.Version != expectedVersion ||
            release.MinimumDataSchema != 3 || release.MaximumDataSchema != 3)
            throw new InvalidDataException("Signed app metadata does not match the completed installer or current schema.");
        using var stream = installer.OpenRead();
        var digest = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
        if (!digest.Equals(release.InstallerSha256, StringComparison.Ordinal))
            throw new InvalidDataException("Signed app metadata installer digest does not match.");
        Console.WriteLine("App envelope signature, temporal policy, payload and installer binding validated.");
        exitCode = 0;
        return true;
    }

    private static string Required(string[] args, string option)
    {
        var index = Array.IndexOf(args, option);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] :
            throw new ArgumentException("Missing required option " + option + ".");
    }
}
