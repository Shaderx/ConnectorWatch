using System.Text.Json;

namespace ConnectorWatch;

/// <summary>Stable per-user locations used by the installed daemon, GUI, installer and updater.</summary>
public static class DeploymentPaths
{
    public const string ProductName = "ConnectorWatch";

    public static string UserStateRoot(string? localApplicationData = null) =>
        Path.Combine(localApplicationData ?? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), ProductName);

    public static string UserProgramsRoot(string? localApplicationData = null) =>
        Path.Combine(localApplicationData ?? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Programs", ProductName);

    public static string InstalledConfigPath(string? localApplicationData = null) =>
        Path.Combine(UserStateRoot(localApplicationData), "config.json");

    public static string InstalledDataDirectory(string? localApplicationData = null) =>
        Path.Combine(UserStateRoot(localApplicationData), "data");

    public static string UpdateCacheDirectory(string? localApplicationData = null) =>
        Path.Combine(UserStateRoot(localApplicationData), "updates");

    public static bool IsInstalledLocation(string? applicationBaseDirectory = null, string? localApplicationData = null)
    {
        var baseDirectory = Normalize(applicationBaseDirectory ?? AppContext.BaseDirectory);
        var programsRoot = Normalize(UserProgramsRoot(localApplicationData));
        return IsWithin(baseDirectory, programsRoot);
    }

    /// <summary>
    /// Resolves an explicit path first. Installed builds use the external per-user configuration;
    /// portable builds continue to use the configuration beside the executable.
    /// </summary>
    public static string ResolveConfigPath(string? explicitPath = null,
        string? applicationBaseDirectory = null, string? localApplicationData = null)
    {
        if (!string.IsNullOrWhiteSpace(explicitPath)) return Path.GetFullPath(explicitPath);
        var baseDirectory = Path.GetFullPath(applicationBaseDirectory ?? AppContext.BaseDirectory);
        return IsInstalledLocation(baseDirectory, localApplicationData)
            ? Path.GetFullPath(InstalledConfigPath(localApplicationData))
            : Path.GetFullPath(Path.Combine(baseDirectory, "config.json"));
    }

    public static string ResolveDataDirectory(string configPath, string configuredDataDirectory)
    {
        if (string.IsNullOrWhiteSpace(configPath))
            throw new ArgumentException("A configuration path is required.", nameof(configPath));
        if (string.IsNullOrWhiteSpace(configuredDataDirectory))
            throw new InvalidDataException("The configuration DataDirectory is empty.");
        return Path.GetFullPath(configuredDataDirectory, Path.GetDirectoryName(Path.GetFullPath(configPath))!);
    }

    public static string ResolveDataDirectoryFromConfig(string configPath)
    {
        using var document = JsonDocument.Parse(File.ReadAllBytes(configPath), new JsonDocumentOptions
        {
            AllowTrailingCommas = false,
            CommentHandling = JsonCommentHandling.Disallow,
            MaxDepth = 32,
        });
        if (!document.RootElement.TryGetProperty("DataDirectory", out var value) || value.ValueKind != JsonValueKind.String)
            throw new InvalidDataException("The selected configuration has no DataDirectory string.");
        return ResolveDataDirectory(configPath, value.GetString()!);
    }

    internal static bool IsWithin(string candidate, string root)
    {
        candidate = Normalize(candidate);
        root = Normalize(root);
        if (string.Equals(candidate, root, PathComparison)) return true;
        return candidate.StartsWith(root + Path.DirectorySeparatorChar, PathComparison);
    }

    internal static string Normalize(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    internal static StringComparison PathComparison => OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
}
