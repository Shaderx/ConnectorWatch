using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ConnectorWatch;

public sealed record PortableMigrationOptions(
    string SourceConfigPath,
    string DestinationConfigPath,
    string DestinationDataDirectory,
    long MaximumBytes = 50L * 1024 * 1024 * 1024,
    int MaximumFiles = 250_000,
    TimeSpan? StopTimeout = null);

public sealed record PortableMigrationResult(
    string SourceConfigPath,
    string SourceDataDirectory,
    string DestinationConfigPath,
    string DestinationDataDirectory,
    string BackupDirectory,
    int FilesCopied,
    long BytesCopied);

public interface IDeploymentStopper
{
    Task RequestStopAsync(string dataDirectory, TimeSpan timeout, CancellationToken cancellationToken);
}

public sealed class DeploymentStopCoordinator : IDeploymentStopper
{
    readonly NamedPipeDeploymentStopper daemon = new();
    public async Task RequestStopAsync(string dataDirectory, TimeSpan timeout, CancellationToken cancellationToken)
    {
        await DeploymentCommands.RequestGuiShutdownAsync(dataDirectory, timeout, cancellationToken).ConfigureAwait(false);
        await daemon.RequestStopAsync(dataDirectory, timeout, cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>Sends the daemon's existing identity-bound stop command and waits for its process to exit.</summary>
public sealed class NamedPipeDeploymentStopper : IDeploymentStopper
{
    public async Task RequestStopAsync(string dataDirectory, TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        bounded.CancelAfter(timeout);
        var clientId = "connectorwatch-deployment-" + Guid.NewGuid().ToString("N");
        ControlResponse hello;
        try
        {
            hello = await SendAsync(dataDirectory, new ControlRequest("hello", clientId), bounded.Token)
                .ConfigureAwait(false) ?? throw new IOException("The monitor returned no hello response.");
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or OperationCanceledException)
        {
            // No endpoint and no exclusive monitor lock means there is no active writer to stop.
            if (CanTakeMonitorLock(dataDirectory)) return;
            throw new IOException("ConnectorWatch is still writing data but did not answer its control endpoint.", ex);
        }

        if (!hello.Ok || hello.Protocol != ControlProtocol.Version || hello.Pid <= 0 ||
            !string.Equals(ControlEndpoint.NormalizeDataDirectory(hello.DataDirectory),
                ControlEndpoint.NormalizeDataDirectory(dataDirectory), DeploymentPaths.PathComparison))
            throw new IOException("The monitor returned an incompatible identity response.");

        var stop = await SendAsync(dataDirectory,
            new ControlRequest("stop", clientId, hello.InstanceId), bounded.Token).ConfigureAwait(false);
        if (stop is null || !stop.Ok || stop.InstanceId != hello.InstanceId || stop.Pid != hello.Pid)
            throw new IOException("The monitor did not acknowledge the identity-bound stop request.");

        while (!CanTakeMonitorLock(dataDirectory))
        {
            bounded.Token.ThrowIfCancellationRequested();
            await Task.Delay(100, bounded.Token).ConfigureAwait(false);
        }
    }

    static bool CanTakeMonitorLock(string dataDirectory)
    {
        var path = Path.Combine(dataDirectory, "monitor.lock");
        try
        {
            Directory.CreateDirectory(dataDirectory);
            using var stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            return true;
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    static async Task<ControlResponse?> SendAsync(string dataDirectory, ControlRequest request, CancellationToken token)
    {
        await using var pipe = new NamedPipeClientStream(".", ControlEndpoint.Name(dataDirectory),
            PipeDirection.InOut, PipeOptions.Asynchronous | (OperatingSystem.IsWindows() ? PipeOptions.CurrentUserOnly : PipeOptions.None));
        await pipe.ConnectAsync(token).ConfigureAwait(false);
        await using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 1024, leaveOpen: true) { AutoFlush = true };
        await writer.WriteLineAsync(ControlProtocol.Serialize(request).AsMemory(), token).ConfigureAwait(false);
        using var reader = new StreamReader(pipe, Encoding.UTF8, false, 1024, leaveOpen: true);
        var line = await reader.ReadLineAsync(token).ConfigureAwait(false);
        if (line is null || line.Length > 64 * 1024) return null;
        return JsonSerializer.Deserialize<ControlResponse>(line, ControlProtocol.Json);
    }
}

/// <summary>
/// Imports one portable configuration through verified staging directories. The source is never modified;
/// reparse points and paths escaping the portable root are rejected before a writer is stopped.
/// </summary>
public sealed class PortableMigration(IDeploymentStopper? stopper = null)
{
    const string MigrationMarker = ".connectorwatch-migration.json";
    readonly IDeploymentStopper stopper = stopper ?? new DeploymentStopCoordinator();

    public async Task<PortableMigrationResult> ImportAsync(PortableMigrationOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.MaximumBytes <= 0 || options.MaximumFiles <= 0)
            throw new ArgumentOutOfRangeException(nameof(options), "Migration limits must be positive.");

        var sourceConfig = DeploymentPaths.Normalize(options.SourceConfigPath);
        if (!File.Exists(sourceConfig)) throw new FileNotFoundException("Portable configuration was not found.", sourceConfig);
        var sourceRoot = Path.GetDirectoryName(sourceConfig)!;
        RejectReparseAncestors(sourceRoot);
        RejectReparsePoints(sourceRoot, includeDescendants: false);
        RejectReparsePoints(sourceConfig, includeDescendants: false);

        string rawDataDirectory;
        byte[] configBytes = File.ReadAllBytes(sourceConfig);
        using (var document = JsonDocument.Parse(configBytes, new JsonDocumentOptions
        {
            AllowTrailingCommas = false, CommentHandling = JsonCommentHandling.Disallow, MaxDepth = 32,
        }))
        {
            if (!document.RootElement.TryGetProperty("DataDirectory", out var dataValue) ||
                dataValue.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(dataValue.GetString()))
                throw new InvalidDataException("Portable configuration has no usable DataDirectory.");
            rawDataDirectory = dataValue.GetString()!;
        }

        if (Path.IsPathRooted(rawDataDirectory) || HasParentTraversal(rawDataDirectory))
            throw new InvalidDataException("Portable DataDirectory must be a relative path without parent traversal.");
        var sourceData = DeploymentPaths.Normalize(Path.Combine(sourceRoot, rawDataDirectory));
        if (!DeploymentPaths.IsWithin(sourceData, sourceRoot))
            throw new InvalidDataException("Portable DataDirectory escapes the selected portable directory.");
        if (!Directory.Exists(sourceData)) throw new DirectoryNotFoundException("Portable data directory was not found: " + sourceData);
        RejectReparsePoints(sourceData, includeDescendants: true);

        var destinationConfig = Path.GetFullPath(options.DestinationConfigPath);
        var destinationData = DeploymentPaths.Normalize(options.DestinationDataDirectory);
        RejectReparseAncestors(Path.GetDirectoryName(destinationConfig)!);
        RejectReparseAncestors(Path.GetDirectoryName(destinationData)!);
        if (DeploymentPaths.IsWithin(destinationData, sourceRoot) || DeploymentPaths.IsWithin(sourceRoot, destinationData))
            throw new InvalidDataException("Portable source and installed destination must be disjoint directories.");
        bool pristineDestination = IsPristineDestination(destinationConfig, destinationData);
        if ((File.Exists(destinationConfig) || Directory.Exists(destinationData)) && !pristineDestination &&
            !IsRecoverablePublishedData(destinationData, sourceConfig, sourceData))
            throw new IOException("The installed destination already contains configuration or data; migration will not overwrite it.");

        var stopTimeout = options.StopTimeout ?? TimeSpan.FromSeconds(20);
        await stopper.RequestStopAsync(sourceData, stopTimeout, cancellationToken).ConfigureAwait(false);
        if (pristineDestination && (File.Exists(destinationConfig) || Directory.Exists(destinationData)))
            await stopper.RequestStopAsync(destinationData, stopTimeout, cancellationToken).ConfigureAwait(false);
        // Hold the writer lock for the complete snapshot and publish operation. A portable daemon
        // restarted by the user cannot race either verified copy.
        using var sourceWriterLock = new FileStream(Path.Combine(sourceData, "monitor.lock"),
            FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        // The GUI can edit configuration without taking monitor.lock. It has now closed, so take a
        // fresh configuration snapshot and ensure it still identifies the validated source tree.
        configBytes = File.ReadAllBytes(sourceConfig);
        using (var stoppedConfig = JsonDocument.Parse(configBytes))
        {
            if (!stoppedConfig.RootElement.TryGetProperty("DataDirectory", out var stoppedDataValue) ||
                stoppedDataValue.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(stoppedDataValue.GetString()))
                throw new InvalidDataException("Portable configuration changed while stopping its writers.");
            var stoppedRaw = stoppedDataValue.GetString()!;
            if (Path.IsPathRooted(stoppedRaw) || HasParentTraversal(stoppedRaw) ||
                !string.Equals(DeploymentPaths.Normalize(Path.Combine(sourceRoot, stoppedRaw)), sourceData,
                    DeploymentPaths.PathComparison))
                throw new InvalidDataException("Portable DataDirectory changed while stopping its writers; retry the import.");
        }

        var stateRoot = Path.GetDirectoryName(destinationConfig)!;
        Directory.CreateDirectory(stateRoot);
        if (IsRecoverablePublishedData(destinationData, sourceConfig, sourceData))
            return CompleteInterruptedPublish(sourceConfig, sourceData, destinationConfig, destinationData, configBytes);
        var transaction = Guid.NewGuid().ToString("N");
        var stageRoot = Path.Combine(stateRoot, ".migration-" + transaction);
        var stagedData = Path.Combine(stageRoot, "data");
        var backupRoot = Path.Combine(stateRoot, "migration-backups", DateTimeOffset.UtcNow.ToString("yyyyMMddTHHmmssfffZ") + "-" + transaction);
        Directory.CreateDirectory(stagedData);
        Directory.CreateDirectory(backupRoot);

        try
        {
            var staged = CopyTreeVerified(sourceData, stagedData, options.MaximumBytes, options.MaximumFiles, cancellationToken);
            var backupData = Path.Combine(backupRoot, "data");
            _ = CopyTreeVerified(sourceData, backupData, options.MaximumBytes, options.MaximumFiles, cancellationToken);
            File.WriteAllBytes(Path.Combine(backupRoot, "config.json"), configBytes);

            var marker = JsonSerializer.SerializeToUtf8Bytes(new MigrationPublishMarker(
                Convert.ToHexString(SHA256.HashData(configBytes)), sourceConfig, sourceData, destinationConfig,
                destinationData, backupRoot, staged.Files, staged.Bytes));
            WriteDurable(Path.Combine(stagedData, MigrationMarker), marker);

            var importedConfig = RewriteDataDirectory(configBytes, destinationData);
            var stagedConfig = Path.Combine(stageRoot, "config.json");
            WriteDurable(stagedConfig, importedConfig);
            if (pristineDestination)
            {
                if (File.Exists(destinationConfig)) File.Copy(destinationConfig, Path.Combine(backupRoot, "pristine-installed-config.json"));
                if (Directory.Exists(destinationData))
                {
                    var lockPath = Path.Combine(destinationData, "monitor.lock");
                    if (File.Exists(lockPath) && new FileInfo(lockPath).Length == 0) File.Delete(lockPath);
                    Directory.Delete(destinationData, recursive: false);
                }
            }
            Directory.CreateDirectory(Path.GetDirectoryName(destinationData)!);
            Directory.Move(stagedData, destinationData);
            if (File.Exists(destinationConfig)) File.Delete(destinationConfig);
            AtomicReplaceNew(stagedConfig, destinationConfig);
            File.Delete(Path.Combine(destinationData, MigrationMarker));

            var receipt = JsonSerializer.Serialize(new
            {
                schema_version = 1,
                imported_at_utc = DateTimeOffset.UtcNow,
                source_config = sourceConfig,
                source_data = sourceData,
                destination_config = destinationConfig,
                destination_data = destinationData,
                backup = backupRoot,
                files = staged.Files,
                bytes = staged.Bytes,
            });
            WriteDurable(Path.Combine(backupRoot, "migration-receipt.json"), Encoding.UTF8.GetBytes(receipt));
            return new(sourceConfig, sourceData, destinationConfig, destinationData,
                backupRoot, staged.Files, staged.Bytes);
        }
        catch
        {
            // Staging has no live destination name. It is always safe to remove and retry.
            if (Directory.Exists(stageRoot)) Directory.Delete(stageRoot, recursive: true);
            throw;
        }
        finally
        {
            if (Directory.Exists(stageRoot)) Directory.Delete(stageRoot, recursive: true);
        }
    }

    static bool HasParentTraversal(string relativePath) => relativePath
        .Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries)
        .Any(component => component is "." or "..");

    sealed record MigrationPublishMarker(string SourceConfigSha256, string SourceConfig, string SourceData,
        string DestinationConfig, string DestinationData, string BackupDirectory, int Files, long Bytes);

    static bool IsRecoverablePublishedData(string destinationData, string sourceConfig, string sourceData)
    {
        var markerPath = Path.Combine(destinationData, MigrationMarker);
        if (!File.Exists(markerPath)) return false;
        try
        {
            var marker = JsonSerializer.Deserialize<MigrationPublishMarker>(File.ReadAllBytes(markerPath));
            return marker is not null &&
                string.Equals(DeploymentPaths.Normalize(marker.SourceConfig), sourceConfig, DeploymentPaths.PathComparison) &&
                string.Equals(DeploymentPaths.Normalize(marker.SourceData), sourceData, DeploymentPaths.PathComparison) &&
                string.Equals(DeploymentPaths.Normalize(marker.DestinationData), destinationData, DeploymentPaths.PathComparison);
        }
        catch { return false; }
    }

    static PortableMigrationResult CompleteInterruptedPublish(string sourceConfig, string sourceData,
        string destinationConfig, string destinationData, byte[] configBytes)
    {
        var markerPath = Path.Combine(destinationData, MigrationMarker);
        var marker = JsonSerializer.Deserialize<MigrationPublishMarker>(File.ReadAllBytes(markerPath))
            ?? throw new InvalidDataException("Interrupted migration marker is invalid.");
        if (!string.Equals(DeploymentPaths.Normalize(marker.DestinationConfig), Path.GetFullPath(destinationConfig), DeploymentPaths.PathComparison) ||
            !CryptographicOperations.FixedTimeEquals(Convert.FromHexString(marker.SourceConfigSha256), SHA256.HashData(configBytes)))
            throw new InvalidDataException("Interrupted migration does not match the selected source and destination.");
        VerifyTree(sourceData, destinationData);
        var importedConfig = RewriteDataDirectory(configBytes, destinationData);
        if (!File.Exists(destinationConfig))
        {
            var temporary = destinationConfig + ".migration-new";
            WriteDurable(temporary, importedConfig);
            AtomicReplaceNew(temporary, destinationConfig);
        }
        File.Delete(markerPath);
        return new(sourceConfig, sourceData, destinationConfig, destinationData,
            marker.BackupDirectory, marker.Files, marker.Bytes);
    }

    static bool IsPristineDestination(string destinationConfig, string destinationData)
    {
        if (!File.Exists(destinationConfig))
            return !Directory.Exists(destinationData) || !Directory.EnumerateFileSystemEntries(destinationData).Any();
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllBytes(destinationConfig));
            if (document.RootElement.TryGetProperty("GpuUuid", out var gpu) &&
                gpu.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(gpu.GetString())) return false;
            if (!string.Equals(DeploymentPaths.ResolveDataDirectoryFromConfig(destinationConfig), destinationData,
                DeploymentPaths.PathComparison)) return false;
            return !Directory.Exists(destinationData) || Directory.EnumerateFileSystemEntries(destinationData)
                .All(path => string.Equals(Path.GetFileName(path), "monitor.lock", StringComparison.OrdinalIgnoreCase) &&
                    File.Exists(path) && new FileInfo(path).Length == 0);
        }
        catch { return false; }
    }

    static byte[] RewriteDataDirectory(byte[] configBytes, string destinationData)
    {
        using var document = JsonDocument.Parse(configBytes);
        var values = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var property in document.RootElement.EnumerateObject()) values.Add(property.Name, property.Value.Clone());
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            foreach (var pair in values)
            {
                writer.WritePropertyName(pair.Key);
                if (string.Equals(pair.Key, "DataDirectory", StringComparison.Ordinal)) writer.WriteStringValue(destinationData);
                else pair.Value.WriteTo(writer);
            }
            writer.WriteEndObject();
        }
        return stream.ToArray();
    }

    static (int Files, long Bytes) CopyTreeVerified(string source, string destination,
        long maximumBytes, int maximumFiles, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(destination);
        int files = 0;
        long bytes = 0;
        foreach (var sourceFile in EnumerateSafeFiles(source))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.Equals(Path.GetRelativePath(source, sourceFile), "monitor.lock", StringComparison.OrdinalIgnoreCase)) continue;
            RejectReparsePoints(sourceFile, includeDescendants: false);
            var relative = Path.GetRelativePath(source, sourceFile);
            if (Path.IsPathRooted(relative) || HasParentTraversal(relative))
                throw new InvalidDataException("A source file escaped the selected data directory.");
            var destinationFile = Path.GetFullPath(Path.Combine(destination, relative));
            if (!DeploymentPaths.IsWithin(destinationFile, destination))
                throw new InvalidDataException("A destination path escaped migration staging.");
            files++;
            bytes = checked(bytes + new FileInfo(sourceFile).Length);
            if (files > maximumFiles || bytes > maximumBytes)
                throw new IOException("Portable data exceeds the configured migration limit.");
            Directory.CreateDirectory(Path.GetDirectoryName(destinationFile)!);
            var temporary = destinationFile + ".copying";
            using (var input = new FileStream(sourceFile, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                1024 * 1024, FileOptions.WriteThrough))
                input.CopyTo(output, 1024 * 1024);
            if (!CryptographicOperations.FixedTimeEquals(Hash(sourceFile), Hash(temporary)))
                throw new IOException("Migration verification failed for " + relative);
            File.Move(temporary, destinationFile);
        }
        return (files, bytes);
    }

    static void VerifyTree(string source, string destination)
    {
        var sourceFiles = EnumerateSafeFiles(source)
            .Where(path => !string.Equals(Path.GetRelativePath(source, path), "monitor.lock", StringComparison.OrdinalIgnoreCase))
            .ToDictionary(path => Path.GetRelativePath(source, path), path => path, StringComparer.OrdinalIgnoreCase);
        var destinationFiles = EnumerateSafeFiles(destination)
            .Where(path => !string.Equals(Path.GetRelativePath(destination, path), MigrationMarker, StringComparison.OrdinalIgnoreCase))
            .ToDictionary(path => Path.GetRelativePath(destination, path), path => path, StringComparer.OrdinalIgnoreCase);
        if (!sourceFiles.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(destinationFiles.Keys))
            throw new IOException("Interrupted migration data set does not match its source.");
        foreach (var pair in sourceFiles)
            if (!CryptographicOperations.FixedTimeEquals(Hash(pair.Value), Hash(destinationFiles[pair.Key])))
                throw new IOException("Interrupted migration verification failed for " + pair.Key);
    }

    static byte[] Hash(string path)
    {
        using var stream = File.OpenRead(path);
        return SHA256.HashData(stream);
    }

    static void RejectReparsePoints(string path, bool includeDescendants)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("Portable migration does not follow links or reparse points: " + path);
        if (!includeDescendants || !Directory.Exists(path)) return;
        _ = EnumerateSafeFiles(path).ToArray();
    }

    static void RejectReparseAncestors(string path)
    {
        var current = new DirectoryInfo(Path.GetFullPath(path));
        while (current is not null)
        {
            if (current.Exists && (current.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Portable migration does not use paths below links or reparse points: " + current.FullName);
            current = current.Parent;
        }
    }

    internal static IEnumerable<string> EnumerateSafeFiles(string root)
    {
        root = DeploymentPaths.Normalize(root);
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("File traversal does not follow links or reparse points: " + directory);
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory, "*", SearchOption.TopDirectoryOnly))
            {
                var attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("File traversal does not follow links or reparse points: " + entry);
                if ((attributes & FileAttributes.Directory) != 0) pending.Push(entry);
                else yield return entry;
            }
        }
    }

    static void WriteDurable(string path, byte[] bytes)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None,
            4096, FileOptions.WriteThrough);
        stream.Write(bytes);
        stream.Flush(flushToDisk: true);
    }

    static void AtomicReplaceNew(string stagedPath, string destinationPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
        if (File.Exists(destinationPath)) throw new IOException("Migration destination appeared during import.");
        File.Move(stagedPath, destinationPath);
    }
}
