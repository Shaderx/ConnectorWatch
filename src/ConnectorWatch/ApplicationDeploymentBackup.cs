using System.Security.Cryptography;
using System.Text.Json;

namespace ConnectorWatch;

public sealed record ApplicationBackupFile(string Path, long Size, string Sha256);
public sealed record ApplicationBackupManifest(int SchemaVersion, DateTimeOffset CreatedAtUtc,
    string SourceDirectory, string RestoreDirectory, string ApplicationVersion, ApplicationBackupFile[] Files);
public sealed record ApplicationRestoreResult(string RestoreDirectory, string? ReplacedDirectory, int FilesRestored);

/// <summary>Creates and verifies a recoverable snapshot of installed program files before Inno replaces them.</summary>
public static class ApplicationDeploymentBackup
{
    const string ManifestName = "application-backup.json";

    public static string Create(string sourceDirectory, string backupRoot, string restoreDirectory, string applicationVersion)
    {
        sourceDirectory = DeploymentPaths.Normalize(sourceDirectory);
        restoreDirectory = DeploymentPaths.Normalize(restoreDirectory);
        backupRoot = DeploymentPaths.Normalize(backupRoot);
        if (!Directory.Exists(sourceDirectory)) throw new DirectoryNotFoundException(sourceDirectory);
        if (DeploymentPaths.IsWithin(backupRoot, sourceDirectory) || DeploymentPaths.IsWithin(sourceDirectory, backupRoot))
            throw new InvalidDataException("Application backup and source directories must be disjoint.");
        var final = Path.Combine(backupRoot, applicationVersion + "-" + DateTimeOffset.UtcNow.ToString("yyyyMMddTHHmmssfffZ"));
        var stage = final + ".staging-" + Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(stage);
        try
        {
            var files = new List<ApplicationBackupFile>();
            foreach (var source in PortableMigration.EnumerateSafeFiles(sourceDirectory))
            {
                if ((File.GetAttributes(source) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("Application backup does not follow reparse points.");
                var relative = Path.GetRelativePath(sourceDirectory, source);
                if (relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Any(part => part is "." or ".."))
                    throw new InvalidDataException("Application backup path escaped its source.");
                var destination = Path.Combine(stage, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.Copy(source, destination, overwrite: false);
                var sourceHash = Hash(source);
                var destinationHash = Hash(destination);
                if (!CryptographicOperations.FixedTimeEquals(sourceHash, destinationHash))
                    throw new IOException("Application backup verification failed: " + relative);
                files.Add(new(relative.Replace('\\', '/'), new FileInfo(source).Length,
                    Convert.ToHexString(sourceHash).ToLowerInvariant()));
            }
            var manifest = new ApplicationBackupManifest(1, DateTimeOffset.UtcNow, sourceDirectory,
                restoreDirectory, applicationVersion, files.OrderBy(file => file.Path, StringComparer.Ordinal).ToArray());
            File.WriteAllBytes(Path.Combine(stage, ManifestName), JsonSerializer.SerializeToUtf8Bytes(manifest));
            Verify(stage);
            Directory.CreateDirectory(backupRoot);
            Directory.Move(stage, final);
            return final;
        }
        catch
        {
            if (Directory.Exists(stage)) Directory.Delete(stage, recursive: true);
            throw;
        }
    }

    public static ApplicationBackupManifest Verify(string backupDirectory)
    {
        backupDirectory = DeploymentPaths.Normalize(backupDirectory);
        var manifest = JsonSerializer.Deserialize<ApplicationBackupManifest>(
            File.ReadAllBytes(Path.Combine(backupDirectory, ManifestName))) ?? throw new InvalidDataException("Application backup manifest is invalid.");
        if (manifest.SchemaVersion != 1 || manifest.Files.Length == 0)
            throw new InvalidDataException("Application backup manifest schema or file list is invalid.");
        var expected = manifest.Files.ToDictionary(file => file.Path.Replace('/', Path.DirectorySeparatorChar), StringComparer.OrdinalIgnoreCase);
        var actual = PortableMigration.EnumerateSafeFiles(backupDirectory)
            .Where(path => !string.Equals(Path.GetFileName(path), ManifestName, StringComparison.OrdinalIgnoreCase))
            .ToDictionary(path => Path.GetRelativePath(backupDirectory, path), StringComparer.OrdinalIgnoreCase);
        if (!expected.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(actual.Keys))
            throw new IOException("Application backup file set does not match its manifest.");
        foreach (var pair in expected)
        {
            var info = new FileInfo(actual[pair.Key]);
            if (info.Length != pair.Value.Size || !CryptographicOperations.FixedTimeEquals(Hash(info.FullName), Convert.FromHexString(pair.Value.Sha256)))
                throw new IOException("Application backup verification failed: " + pair.Key);
        }
        return manifest;
    }

    /// <summary>Restores a verified pre-upgrade snapshot. The replaced directory is retained for recovery.</summary>
    public static ApplicationRestoreResult Restore(string backupDirectory)
    {
        backupDirectory = DeploymentPaths.Normalize(backupDirectory);
        var manifest = Verify(backupDirectory);
        var restoreDirectory = DeploymentPaths.Normalize(manifest.RestoreDirectory);
        var parent = Path.GetDirectoryName(restoreDirectory)!;
        var name = Path.GetFileName(restoreDirectory);
        var transaction = Guid.NewGuid().ToString("N");
        var stage = Path.Combine(parent, "." + name + ".restore-" + transaction);
        string? replaced = null;
        Directory.CreateDirectory(stage);
        try
        {
            foreach (var file in manifest.Files)
            {
                var relative = file.Path.Replace('/', Path.DirectorySeparatorChar);
                if (Path.IsPathRooted(relative) || relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Any(part => part is "." or ".."))
                    throw new InvalidDataException("Application backup contains an unsafe restore path.");
                var source = Path.Combine(backupDirectory, relative);
                var destination = Path.Combine(stage, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.Copy(source, destination, overwrite: false);
                if (!CryptographicOperations.FixedTimeEquals(Hash(destination), Convert.FromHexString(file.Sha256)))
                    throw new IOException("Application restore staging verification failed: " + relative);
            }
            if (Directory.Exists(restoreDirectory))
            {
                replaced = Path.Combine(parent, "." + name + ".replaced-" + transaction);
                Directory.Move(restoreDirectory, replaced);
            }
            try { Directory.Move(stage, restoreDirectory); }
            catch
            {
                if (replaced is not null && !Directory.Exists(restoreDirectory)) Directory.Move(replaced, restoreDirectory);
                throw;
            }
            return new(restoreDirectory, replaced, manifest.Files.Length);
        }
        catch
        {
            if (Directory.Exists(stage)) Directory.Delete(stage, recursive: true);
            throw;
        }
    }

    static byte[] Hash(string path)
    {
        using var stream = File.OpenRead(path);
        return SHA256.HashData(stream);
    }
}
