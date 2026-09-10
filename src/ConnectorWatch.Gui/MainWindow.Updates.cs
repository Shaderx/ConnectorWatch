using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using ConnectorWatch;

namespace ConnectorWatch.Gui;

public sealed partial class MainWindow
{
    private readonly Button updateButton = Button("Check for app updates");
    private readonly CancellationTokenSource updateEnding = new();
    private bool updateBusy;
    // Schema 3 is the existing status/storage generation. This release does not
    // migrate historical recordings or reference artifacts to a newer schema.
    internal const int CurrentDataSchema = 3;

    private async Task CheckForAppUpdate(bool interactive)
    {
        if (demo || updateBusy || exiting) return;
        updateBusy = true; updateButton.IsEnabled = false;
        try
        {
            using var http = new HttpClient();
            var service = AppUpdateService.CreateDefault(http);
            var current = typeof(App).Assembly.GetName().Version!.ToString(3);
            var check = await service.CheckAsync(current, CurrentDataSchema, DateTimeOffset.UtcNow, updateEnding.Token);
            if (check.Disposition == AppUpdateDisposition.Current)
            {
                updateButton.Content = "Check for app updates";
                if (interactive) MessageBox.Show(this, check.Detail, "App updates", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            if (check.Disposition == AppUpdateDisposition.IncompatibleDataSchema)
            {
                updateButton.Content = "Update needs migration";
                if (interactive) MessageBox.Show(this, check.Detail, "App updates", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            updateButton.Content = $"Update to {check.Release.Version}";
            if (!interactive) return;
            var prompt = check.Detail + "\n\n" + check.Release.ReleaseNotes +
                "\n\nDownload and verify the installer? Monitoring will continue during the download.";
            if (MessageBox.Show(this, prompt, "ConnectorWatch update", MessageBoxButton.YesNo,
                    MessageBoxImage.Information, MessageBoxResult.No) != MessageBoxResult.Yes) return;
            updateButton.Content = "Downloading update…";
            var installer = await service.DownloadInstallerAsync(check, updateEnding.Token);
            if (MessageBox.Show(this,
                    "The installer has been verified. Restart ConnectorWatch and install the update now? Your configuration and history will be preserved.",
                    "Restart to update", MessageBoxButton.YesNo, MessageBoxImage.Information, MessageBoxResult.No) != MessageBoxResult.Yes)
            {
                updateButton.Content = "Update ready"; return;
            }
            using var process = service.LaunchInstaller(installer, check, configPath);
            await ExitGui();
        }
        catch (OperationCanceledException) when (updateEnding.IsCancellationRequested) { }
        catch (Exception ex)
        {
            updateButton.Content = "Check for app updates";
            GuiLog.Current.Write("app_update_error", exception: ex, throttle: true);
            if (interactive && !exiting)
                MessageBox.Show(this, "The update could not be verified or installed. " + ex.Message,
                    "App updates unavailable", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        finally { updateBusy = false; updateButton.IsEnabled = true; }
    }
}
