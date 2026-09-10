using System.Diagnostics;
using System.IO.Pipes;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ConnectorWatch;

namespace ConnectorWatch.Gui;

public sealed class GuiConfig
{
    public string GpuUuid { get; set; } = "";
    public string DataDirectory { get; set; } = "data";
    public double MaxAgeSeconds { get; set; } = 5;
    public double SampleSeconds { get; set; } = 1;
    public int MinAnalysisWatts { get; set; } = 100;
    public int BinWatts { get; set; } = 25;
    public int BaselineSamples { get; set; } = 300;
    public int WindowSamples { get; set; } = 60;
    public double ShiftVolts { get; set; } = .2;
    public double SuddenDroopVolts { get; set; } = .25;
}
public sealed class GuiSettings
{
    public double Width { get; set; } = 1120;
    public double Height { get; set; } = 1010;
    public double Left { get; set; } = -1;
    public double Top { get; set; } = -1;
    public int RangeMinutes { get; set; } = 15;
    public bool ShowPcie { get; set; }
    public int ConfidenceRangeDays { get; set; } = 30;
    public string ConfidenceCohort { get; set; } = "";
    public bool CloseTipShown { get; set; }
    public string[] Acknowledged { get; set; } = Array.Empty<string>();
    public Incident[] LocalIncidents { get; set; } = Array.Empty<Incident>();
}
public sealed class ControlClient
{
    readonly string data;
    readonly string id = Guid.NewGuid().ToString("N");
    public ControlResponse? Identity { get; private set; }
    public ControlClient(string data) => this.data = data;
    public async Task<ControlResponse?> Send(string command)
    {
        try
        {
            using var timeout = new CancellationTokenSource(900);
            using var pipe = new NamedPipeClientStream(".", ControlEndpoint.Name(data), PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            await pipe.ConnectAsync(timeout.Token);
            using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 1024, true) { AutoFlush = true };
            if (command != "hello" && Identity == null) return null;
            await writer.WriteLineAsync(ControlProtocol.Serialize(new ControlRequest(command, id, command == "hello" ? null : Identity?.InstanceId)).AsMemory(), timeout.Token);
            var line = await ReadResponse(pipe, timeout.Token);
            if (line == null || line.Length > 4 * 1024 * 1024) return null;
            var r = JsonSerializer.Deserialize<ControlResponse>(line, ControlProtocol.Json);
            if (r == null || r.Protocol != 1 || r.Pid <= 0 || string.IsNullOrEmpty(r.InstanceId) || !string.Equals(ControlEndpoint.NormalizeDataDirectory(r.DataDirectory), ControlEndpoint.NormalizeDataDirectory(data), StringComparison.OrdinalIgnoreCase)) return null;
            if (command != "hello" && (Identity == null || Identity.InstanceId != r.InstanceId || Identity.Pid != r.Pid)) return null;
            if (command == "hello") Identity = r;
            return r.Ok ? r : null;
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or UnauthorizedAccessException or JsonException) { GuiLog.Current.Write("control_error", new { command, data }, ex, throttle: true); return null; }
    }
    static async Task<string?> ReadResponse(Stream stream, CancellationToken token)
    {
        var chunk = new byte[1024]; using var buffer = new MemoryStream(512);
        while (true)
        {
            int read = await stream.ReadAsync(chunk, token); if (read == 0) return null;
            int newline = Array.IndexOf(chunk, (byte)'\n', 0, read); int length = newline < 0 ? read : newline;
            if (buffer.Length + length > 4 * 1024 * 1024) return null;
            buffer.Write(chunk, 0, length);
            if (newline >= 0) return Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
        }
    }
}

public sealed class App : Application
{
    Mutex? single;
    readonly CancellationTokenSource ending = new();
    public static string? Option(string[] args, string name) { int i = Array.IndexOf(args, name); return i >= 0 && i + 1 < args.Length ? args[i + 1] : null; }
    [STAThread]
    public static int Main(string[] args)
    {
        try
        {
            if (args.Contains("--self-test")) { GuiTests.Run(Option(args, "--test-output")); return 0; }
            GuiLog.Current.Write("gui_start", new { executable = Environment.ProcessPath });
            AppDomain.CurrentDomain.UnhandledException += (_, e) => GuiLog.Current.Write("unhandled_exception", new { e.IsTerminating }, e.ExceptionObject as Exception);
            var app = new App { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            if (!SystemParameters.HighContrast) app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/ConnectorWatch.Gui;component/Theme.xaml", UriKind.Relative) });
            app.Startup += async (_, _) => await app.Start(args);
            app.DispatcherUnhandledException += (_, e) =>
            {
                GuiLog.Current.Write("dispatcher_exception", exception: e.Exception);
                MessageBox.Show(e.Exception.Message, "ConnectorWatch dashboard", MessageBoxButton.OK, MessageBoxImage.Error);
                e.Handled = true;
            };
            return app.Run();
        }
        catch (Exception ex)
        {
            GuiLog.Current.Write("main_error", exception: ex);
            var output = Option(args, "--test-output");
            if (output != null) File.WriteAllText(output, ex.ToString());
            else MessageBox.Show(ex.Message, "ConnectorWatch", MessageBoxButton.OK, MessageBoxImage.Error);
            return 1;
        }
    }
    async Task Start(string[] args)
    {
        try
        {
            bool demo = args.Contains("--demo");
            string configPath = DeploymentPaths.ResolveConfigPath(Option(args, "--config"));
            var config = demo ? new GuiConfig { GpuUuid = "Demonstration · synthetic readings" } : JsonSerializer.Deserialize<GuiConfig>(TelemetryStore.ReadShared(configPath)) ?? throw new InvalidDataException("Invalid configuration");
            string data = demo ? Path.Combine(Path.GetTempPath(), "ConnectorWatch-demo") : Path.GetFullPath(config.DataDirectory, Path.GetDirectoryName(configPath)!);
            GuiLog.Current.Write("configuration_loaded", new { configPath, data, config.MaxAgeSeconds, config.SampleSeconds, demo });
            string endpoint = ControlEndpoint.Name(data) + "-gui";
            string settings = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ConnectorWatch", endpoint + ".json");
            single = new Mutex(true, "Local\\" + endpoint, out bool owns);
            if (!owns)
            {
                try { using var client = new NamedPipeClientStream(".", endpoint, PipeDirection.Out, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly); using var t = new CancellationTokenSource(1500); await client.ConnectAsync(t.Token); await client.WriteAsync(new byte[] { 1 }, t.Token); } catch (Exception ex) when (ex is IOException or OperationCanceledException) { }
                Shutdown(); return;
            }
            var window = new MainWindow(configPath, config, data, settings, demo, args.Contains("--no-start"), args.Contains("--render"));
            MainWindow = window;
            window.QuietTest = args.Contains("--integration-ui-test") || args.Contains("--ui-self-test") || args.Contains("--ui-memory-test");
            SessionEnding += (_, _) => window.SessionEnding();
            _ = ListenActivation(endpoint, window);
            window.Show();
            await window.Initialize();
            if (args.Contains("--ui-memory-test"))
            {
                if (!demo) throw new InvalidOperationException("UI memory test requires synthetic mode.");
                File.WriteAllText(Option(args, "--test-output")!, await window.RunUiMemoryChecks());
                await window.ExitGui(); return;
            }
            if (args.Contains("--integration-ui-test"))
            {
                if (demo) throw new InvalidOperationException("Integration test requires a real daemon control endpoint.");
                var checks = await window.RunIntegrationChecks();
                File.WriteAllText(Option(args, "--test-output")!, JsonSerializer.Serialize(new { passed = checks.Length, checks }));
                await window.ExitGui(); return;
            }
            if (args.Contains("--ui-self-test"))
            {
                if (!demo) throw new InvalidOperationException("UI self-test requires --demo; it must not access a live daemon.");
                var checks = await window.RunUiChecks();
                File.WriteAllText(Option(args, "--test-output")!, JsonSerializer.Serialize(new { passed = checks.Length, checks }));
                await window.ExitGui(); return;
            }
            if (args.Contains("--tray")) window.Hide();
            if (args.Contains("--render"))
            {
                await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                window.UpdateLayout();
                var visual = args.Contains("--render-confidence") ? window.ConfidencePreview! : args.Contains("--render-electrical") ? window.ElectricalTrendPreview! : (FrameworkElement)window.Content;
                var bmp = new RenderTargetBitmap((int)visual.ActualWidth, (int)visual.ActualHeight, 96, 96, PixelFormats.Pbgra32);
                if (args.Contains("--render-electrical") || args.Contains("--render-confidence"))
                {
                    var drawing = new DrawingVisual();
                    using (var dc = drawing.RenderOpen()) dc.DrawRectangle(new VisualBrush(visual), null, new Rect(0, 0, visual.ActualWidth, visual.ActualHeight));
                    bmp.Render(drawing);
                }
                else bmp.Render(visual);
                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bmp));
                using (var f = File.Create(Option(args, "--render")!)) encoder.Save(f);
                await window.ExitGui();
            }
        }
        catch (Exception ex)
        {
            GuiLog.Current.Write("startup_error", exception: ex);
            if (Option(args, "--test-output") is string report) File.WriteAllText(report, ex.ToString());
            else if (Option(args, "--render") is string preview) File.WriteAllText(preview + ".error.txt", ex.ToString());
            else MessageBox.Show(ex.Message, "ConnectorWatch startup", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }
    async Task ListenActivation(string endpoint, MainWindow window)
    {
        while (!ending.IsCancellationRequested)
        {
            try
            {
                using var server = new NamedPipeServerStream(endpoint, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await server.WaitForConnectionAsync(ending.Token);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ending.Token); timeout.CancelAfter(1500);
                var bytes = new byte[1];
                if (await server.ReadAsync(bytes, timeout.Token) > 0)
                {
                    if (bytes[0] == 2) await (await Dispatcher.InvokeAsync(window.ExitGui));
                    else if (bytes[0] == 1) await Dispatcher.InvokeAsync(window.Restore);
                }
            }
            catch (OperationCanceledException) { }
            catch (IOException) { await Task.Delay(500); }
        }
    }
    protected override void OnExit(ExitEventArgs e) { ending.Cancel(); single?.Dispose(); base.OnExit(e); }
}
