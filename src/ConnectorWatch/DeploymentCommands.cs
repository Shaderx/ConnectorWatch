using System.IO.Pipes;
using System.Text.Json;

namespace ConnectorWatch;

/// <summary>Command dispatcher wired by both application entry points for installer-only coordination.</summary>
public static class DeploymentCommands
{
    public static bool TryRun(string[] args, out int exitCode)
    {
        exitCode = 0;
        if (args.Contains("--deployment-stop", StringComparer.OrdinalIgnoreCase))
        {
            exitCode = StopInstalledAsync(args).GetAwaiter().GetResult();
            return true;
        }
        if (args.Contains("--import-portable", StringComparer.OrdinalIgnoreCase))
        {
            exitCode = ImportPortableAsync(args).GetAwaiter().GetResult();
            return true;
        }
        if (args.Contains("--deployment-backup", StringComparer.OrdinalIgnoreCase))
        {
            exitCode = BackupApplication(args);
            return true;
        }
        if (args.Contains("--deployment-restore", StringComparer.OrdinalIgnoreCase))
        {
            exitCode = RestoreApplicationAsync(args).GetAwaiter().GetResult();
            return true;
        }
        return false;
    }

    static int BackupApplication(string[] args)
    {
        try
        {
            var source = Option(args, "--source-app") ?? AppContext.BaseDirectory;
            var backupRoot = Option(args, "--backup-root") ?? Path.Combine(DeploymentPaths.UserStateRoot(), "application-backups");
            var restore = Option(args, "--restore-app") ?? source;
            var version = Option(args, "--version") ?? typeof(DeploymentCommands).Assembly.GetName().Version?.ToString(3) ?? "unknown";
            var backup = ApplicationDeploymentBackup.Create(source, backupRoot, restore, version);
            Console.WriteLine(JsonSerializer.Serialize(new { ok = true, backup }));
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("ConnectorWatch application backup failed: " + ex.Message);
            return 4;
        }
    }

    static async Task<int> RestoreApplicationAsync(string[] args)
    {
        try
        {
            if (!args.Contains("--confirm-prelaunch-recovery", StringComparer.OrdinalIgnoreCase))
                throw new ArgumentException("Restore requires --confirm-prelaunch-recovery; after first launch use only an authenticated schema-safe rollback.");
            var backup = Option(args, "--backup") ?? throw new ArgumentException("--backup is required.");
            var config = DeploymentPaths.ResolveConfigPath(Option(args, "--config"));
            var data = DeploymentPaths.ResolveDataDirectoryFromConfig(config);
            await new DeploymentStopCoordinator().RequestStopAsync(data, TimeSpan.FromSeconds(30), CancellationToken.None)
                .ConfigureAwait(false);
            var result = ApplicationDeploymentBackup.Restore(backup);
            Console.WriteLine(JsonSerializer.Serialize(result));
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("ConnectorWatch application restore failed: " + ex.Message);
            return 5;
        }
    }

    static async Task<int> StopInstalledAsync(string[] args)
    {
        try
        {
            var configPath = DeploymentPaths.ResolveConfigPath(Option(args, "--config"));
            var dataDirectory = DeploymentPaths.ResolveDataDirectoryFromConfig(configPath);
            var seconds = int.TryParse(Option(args, "--wait-seconds"), out var parsed) ? parsed : 20;
            if (seconds is < 1 or > 120) throw new ArgumentOutOfRangeException(nameof(args), "--wait-seconds must be 1-120.");
            var timeout = TimeSpan.FromSeconds(seconds);
            using var cancellation = new CancellationTokenSource(timeout);
            await RequestGuiShutdownAsync(dataDirectory, timeout, cancellation.Token).ConfigureAwait(false);
            await new NamedPipeDeploymentStopper().RequestStopAsync(dataDirectory, timeout, cancellation.Token).ConfigureAwait(false);
            Console.WriteLine(JsonSerializer.Serialize(new { ok = true, stopped = true, data_directory = dataDirectory }));
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("ConnectorWatch deployment stop failed: " + ex.Message);
            return 2;
        }
    }

    static async Task<int> ImportPortableAsync(string[] args)
    {
        try
        {
            var sourceConfig = Option(args, "--source-config") ??
                throw new ArgumentException("--source-config is required with --import-portable.");
            var destinationConfig = Option(args, "--destination-config") ?? DeploymentPaths.InstalledConfigPath();
            var destinationData = Option(args, "--destination-data") ?? DeploymentPaths.InstalledDataDirectory();
            var result = await new PortableMigration().ImportAsync(new(sourceConfig, destinationConfig, destinationData))
                .ConfigureAwait(false);
            Console.WriteLine(JsonSerializer.Serialize(result));
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("ConnectorWatch portable import failed: " + ex.Message);
            return 3;
        }
    }

    /// <summary>Byte 2 on the existing GUI activation endpoint requests ExitGui and a daemon flush.</summary>
    public static async Task RequestGuiShutdownAsync(string dataDirectory, TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsWindows()) return;
        var endpoint = ControlEndpoint.Name(dataDirectory) + "-gui";
        if (!Mutex.TryOpenExisting("Local\\" + endpoint, out var guiMutex)) return;
        using (guiMutex)
        try
        {
            using var bounded = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            bounded.CancelAfter(timeout);
            using var connect = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            connect.CancelAfter(TimeSpan.FromSeconds(Math.Min(2, timeout.TotalSeconds)));
            await using (var pipe = new NamedPipeClientStream(".", endpoint, PipeDirection.Out,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly))
            {
                try { await pipe.ConnectAsync(connect.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                { throw new TimeoutException("ConnectorWatch dashboard owns its instance mutex but did not answer its shutdown endpoint."); }
                await pipe.WriteAsync(new byte[] { 2 }, bounded.Token).ConfigureAwait(false);
                await pipe.FlushAsync(bounded.Token).ConfigureAwait(false);
            }

            // The GUI owns this mutex for its lifetime. Acquiring it proves shutdown completed.
            while (true)
            {
                bounded.Token.ThrowIfCancellationRequested();
                try
                {
                    if (guiMutex.WaitOne(100)) { guiMutex.ReleaseMutex(); return; }
                }
                catch (AbandonedMutexException) { return; }
                await Task.Yield();
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // A connect timeout means no GUI. Cancellation after a successful connect means it did not exit.
            throw new TimeoutException("ConnectorWatch dashboard did not close for the installer.");
        }
    }

    static string? Option(string[] args, string name)
    {
        int index = Array.FindIndex(args, value => string.Equals(value, name, StringComparison.OrdinalIgnoreCase));
        return index < 0 ? null : index + 1 < args.Length ? args[index + 1] :
            throw new ArgumentException("Missing value for " + name);
    }
}
