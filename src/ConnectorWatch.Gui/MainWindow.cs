using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Forms = System.Windows.Forms;
using Drawing = System.Drawing;

namespace ConnectorWatch.Gui;

public sealed partial class MainWindow : Window
{
    public bool QuietTest { get; set; }
    readonly GuiConfig config;
    readonly string configPath, data, settingsPath;
    readonly bool demo, noStart, render;
    readonly TelemetryStore store;
    readonly ControlClient client;
    GuiSettings settings = new();
    readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromSeconds(1) };
    Forms.NotifyIcon? tray;
    Forms.ContextMenuStrip? trayMenu;
    readonly TextBlock connection = Text("Connecting…", 12), voltage = Text("—", 30), power = Text("—", 30), pcie = Text("—", 30), change = Text("—", 30);
    readonly TextBlock voltageNote = Text("16-pin input", 11), powerNote = Text("Measured rail V × A", 11), referenceNote = Text("Waiting for a reference", 11);
    readonly TextBlock powerLabel = Text("CONNECTOR POWER", 10);
    readonly TextBlock powerPath = Text("", 11), degradation = Text("", 11);
    readonly TextBlock bannerText = Text("", 13), analysisTitle = Text("Waiting for data", 19), analysisBody = Text("", 12), histogramStats = Text("", 11), footer = Text("", 11);
    readonly Border banner = new() { CornerRadius = new CornerRadius(8), Padding = new Thickness(14, 10, 14, 10), Margin = new Thickness(0, 0, 0, 14) };
    readonly ProgressBar progress = new() { Height = 5, Margin = new Thickness(0, 12, 0, 12), Maximum = 300, Foreground = Palette.Cyan, Background = Palette.Line, BorderThickness = new Thickness(0) };
    readonly HistoryPlot history = new() { Height = 170 };
    readonly ElectricalTrendPlot electricalTrend = new() { Height = 190 };
    readonly ConfidencePlot confidencePlot = new() { Height = 180 };
    readonly TextBlock confidenceValue = Text("Learning", 28), confidenceCaption = Text("Gathering comparable days…", 12), confidenceDetails = Text("", 10);
    readonly ComboBox confidenceRange = new() { Width = 100 }, confidenceCohort = new() { MinWidth = 180 };
    readonly ConfidenceHistory? confidenceHistory;
    readonly List<ConfidenceDay> confidenceDemoDays = new();
    bool confidenceReadBusy;
    DateTimeOffset confidenceLastRead;
    bool renderingConfidence;
    IReadOnlyList<ConfidenceDay>? confidenceRenderedDays;
    (string Cohort, int Range, DateTime Day, bool Reading, double ShiftVolts) confidenceRenderedSelection;
    internal FrameworkElement? ConfidencePreview { get; private set; }
    readonly TextBlock electricalTrendSummary = Text("", 11);
    readonly ComboBox electricalTrendBin = new() { MinWidth = 150 }, electricalTrendMode = new() { MinWidth = 155 };
    internal FrameworkElement? ElectricalTrendPreview { get; private set; }
    DateTimeOffset trendBuiltAt;
    (int Range, DateTimeOffset? Focus, int? Bin, bool Initial)? trendSelection;
    readonly HistogramPlot histogram = new() { Height = 130 };
    readonly ComboBox range = new() { Width = 115 }, bins = new() { MinWidth = 170 };
    readonly CheckBox overlay = new() { Content = "PCIe overlay", Foreground = Palette.Muted, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(14, 0, 0, 0) };
    readonly ListBox warnings = new() { Height = 76, Background = Brushes.Transparent, BorderThickness = new Thickness(0), Foreground = Palette.Text };
    readonly Button resumeLive = Button("Back to live");
    readonly BoundedIdSet acknowledged = new(2000);
    readonly BoundedIdSet notified = new(4000);
    readonly BoundedBuffer<Incident> localIncidents = new(200);
    long renderedWarningRevision = -1, renderedAcknowledgementRevision = -1;
    bool renderedAllWarnings;
    List<PointSample> visiblePoints = new();
    List<PointSample> distributionPoints = new();
    bool exiting, busy, initialized, connected, hadFresh, firstPoll = true, lossNotified, allWarnings;
    long lastRefreshStarted;
    DateTimeOffset? focusTime;
    DateTimeOffset lastNotification;
    string operation = "";
    bool awaitingApprovalRefresh;
    DateTimeOffset? approvalCheckBeforeRefresh;
    public MainWindow(string configPath, GuiConfig config, string data, string settingsPath, bool demo, bool noStart, bool render)
    {
        this.configPath = configPath; this.config = config; this.data = data; this.settingsPath = settingsPath; this.demo = demo; this.noStart = noStart; this.render = render;
        store = new(data); client = new(data);
        confidenceHistory = demo ? null : new ConfidenceHistory(data, Path.Combine(data, "confidence-history.json"), config.BinWatts);
        try { if (File.Exists(settingsPath)) settings = JsonSerializer.Deserialize<GuiSettings>(TelemetryStore.ReadShared(settingsPath)) ?? new(); } catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { }
        foreach (var id in settings.Acknowledged ?? Array.Empty<string>()) acknowledged.Add(id);
        if (!demo) { localIncidents.AddRange(settings.LocalIncidents ?? Array.Empty<Incident>()); store.Incidents.AddRange(localIncidents); }
        Title = demo ? "ConnectorWatch — synthetic preview" : "ConnectorWatch";
        Width = Math.Clamp(settings.Width, 900, Math.Max(900, SystemParameters.WorkArea.Width)); Height = Math.Clamp(settings.Height, 740, Math.Max(740, SystemParameters.WorkArea.Height)); MinWidth = 900; MinHeight = 740;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        if (settings.Left >= SystemParameters.VirtualScreenLeft && settings.Top >= SystemParameters.VirtualScreenTop && settings.Left + 200 < SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth && settings.Top + 100 < SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight)
        { WindowStartupLocation = WindowStartupLocation.Manual; Left = settings.Left; Top = settings.Top; }
        Background = Palette.Bg; Foreground = Palette.Text; FontFamily = new FontFamily("Segoe UI"); FontSize = 13;
        Build();
        if (!render) CreateTray();
        Closing += OnClosing;
        timer.Tick += async (_, _) => await Refresh();
        IsVisibleChanged += async (_, _) =>
        {
            if (IsVisible && initialized) await Refresh();
            else if (!IsVisible)
            {
                // A hidden chart must not keep an old 24-hour snapshot alive.
                visiblePoints.Clear(); distributionPoints.Clear(); history.Samples = Array.Empty<PointSample>();
                histogram.Histogram = PlotData.Histogram(Array.Empty<PointSample>());
            }
        };
        SystemParameters.StaticPropertyChanged += ThemeChanged;
    }
    static TextBlock Text(string text, double size = 13) => new() { Text = text, FontSize = size, Foreground = Palette.Text, TextWrapping = TextWrapping.Wrap };
    static Button Button(string label) => new() { Content = label, Background = Palette.Line, Foreground = Palette.Text, BorderThickness = new Thickness(0), Padding = new Thickness(12, 7, 12, 7), Margin = new Thickness(0, 0, 8, 0), Cursor = System.Windows.Input.Cursors.Hand };
    static Border Panel(UIElement child) => new() { Child = child, Background = Palette.Panel, BorderBrush = Palette.Line, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(10), Padding = new Thickness(14) };
    static StackPanel Vertical(params UIElement[] children) { var p = new StackPanel(); foreach (var c in children) p.Children.Add(c); return p; }
    static DockPanel Header(string title, UIElement? right = null)
    {
        var d = new DockPanel { Margin = new Thickness(0, 0, 12, 10) };
        if (right != null) { DockPanel.SetDock(right, Dock.Right); d.Children.Add(right); }
        d.Children.Add(Text(title, 15)); return d;
    }
    void Build()
    {
        var content = new StackPanel { Margin = new Thickness(24, 20, 24, 18) };
        var top = new DockPanel { Margin = new Thickness(0, 0, 0, 18) };
        DockPanel.SetDock(connection, Dock.Right); connection.VerticalAlignment = VerticalAlignment.Center; top.Children.Add(connection);
        var brand = Vertical(Text("ConnectorWatch", 24), Text(demo ? "SYNTHETIC PREVIEW  /  NO HARDWARE ACCESS" : "RTX 5090  /  INPUT RAIL MONITOR", 10));
        ((TextBlock)brand.Children[1]).Foreground = demo ? Palette.Amber : Palette.Muted;
        top.Children.Add(brand); content.Children.Add(top);
        banner.Child = bannerText; content.Children.Add(banner);
        var cards = new Grid { Margin = new Thickness(0, 0, 0, 16) };
        var values = new[] { voltage, power, pcie, change }; var notes = new[] { voltageNote, powerNote, Text("Slot supply", 11), referenceNote };
        string[] labels = { "16-PIN VOLTAGE", "CONNECTOR POWER", "PCIe 12V", "REFERENCE CHANGE" };
        for (int i = 0; i < 4; i++)
        {
            cards.ColumnDefinitions.Add(new ColumnDefinition()); values[i].Margin = new Thickness(0, 7, 0, 4); values[i].Foreground = i == 0 ? Palette.Cyan : i == 2 ? Palette.Violet : Palette.Text;
            var label = i == 1 ? powerLabel : Text(labels[i], 10); label.Foreground = Palette.Muted; notes[i].Foreground = Palette.Muted;
            var card = Panel(Vertical(label, values[i], notes[i])); card.Margin = new Thickness(i == 0 ? 0 : 5, 0, i == 3 ? 0 : 5, 0); Grid.SetColumn(card, i); cards.Children.Add(card);
        }
        content.Children.Add(cards);
        foreach (var text in new[] { "7 days", "30 days", "90 days" }) confidenceRange.Items.Add(text);
        confidenceRange.SelectedIndex = settings.ConfidenceRangeDays == 7 ? 0 : settings.ConfidenceRangeDays == 90 ? 2 : 1;
        settings.ConfidenceRangeDays = confidenceRange.SelectedIndex == 0 ? 7 : confidenceRange.SelectedIndex == 2 ? 90 : 30;
        confidenceRange.SelectionChanged += (_, _) => { settings.ConfidenceRangeDays = confidenceRange.SelectedIndex == 0 ? 7 : confidenceRange.SelectedIndex == 2 ? 90 : 30; RenderConfidence(); SaveSettings(); };
        confidenceCohort.Items.Add("Automatic comparison"); confidenceCohort.SelectedIndex = 0;
        confidenceCohort.SelectionChanged += (_, _) => { if (renderingConfidence) return; settings.ConfidenceCohort = (confidenceCohort.SelectedItem as ConfidenceCohortChoice)?.Key ?? ""; RenderConfidence(); SaveSettings(); };
        var confidenceAdvanced = new Expander { Header = "Comparison details", Foreground = Palette.Muted, Content = Vertical(confidenceCohort, confidenceDetails), Margin = new Thickness(0, 6, 0, 0) };
        var confidencePanel = Panel(Vertical(Header("Electrical degradation confidence", confidenceRange), confidenceValue, confidenceCaption, confidencePlot,
            Text("Higher means stronger evidence of a persistent voltage decline under similar load.\nExperimental evidence score, not a measured probability of hardware damage. Updated from completed days.", 10), confidenceAdvanced));
        ConfidencePreview = confidencePanel; confidencePanel.Margin = new Thickness(0, 0, 0, 16); content.Children.Add(confidencePanel);
        powerPath.Foreground = Palette.Text; degradation.Foreground = Palette.Amber;
        var pathPanel = Panel(Vertical(Header("Power path and data quality"), powerPath, degradation)); pathPanel.Margin = new Thickness(0, 0, 0, 16); content.Children.Add(pathPanel);
        range.Items.Add("15 minutes"); range.Items.Add("1 hour"); range.Items.Add("24 hours");
        range.SelectedIndex = settings.RangeMinutes == 1440 ? 2 : settings.RangeMinutes == 60 ? 1 : 0;
        range.SelectionChanged += (_, _) => { settings.RangeMinutes = range.SelectedIndex == 2 ? 1440 : range.SelectedIndex == 1 ? 60 : 15; if (!busy) RenderData(); };
        overlay.IsChecked = settings.ShowPcie; overlay.Checked += (_, _) => { settings.ShowPcie = true; if (!busy) RenderData(); }; overlay.Unchecked += (_, _) => { settings.ShowPcie = false; if (!busy) RenderData(); };
        var chartOptions = new StackPanel { Orientation = Orientation.Horizontal }; chartOptions.Children.Add(overlay); chartOptions.Children.Add(range);
        range.Margin = new Thickness(14, 0, 0, 0);
        var legend = Text("16-pin input  •  cyan     PCIe supply  •  violet     Alert  •  red", 10); legend.Foreground = Palette.Muted;
        var chartPanel = Panel(Vertical(Header("Voltage history", chartOptions), history, legend)); chartPanel.Margin = new Thickness(0, 0, 0, 16); content.Children.Add(chartPanel);
        electricalTrendBin.Items.Add("Most sampled load"); electricalTrendBin.SelectedIndex = 0;
        electricalTrendMode.Items.Add("Initial observation"); electricalTrendMode.Items.Add("Recorded reference"); electricalTrendMode.SelectedIndex = 0;
        electricalTrendBin.SelectionChanged += (_, _) => { if (!busy) RenderData(); };
        electricalTrendMode.SelectionChanged += (_, _) => { if (!busy) RenderData(); };
        var trendOptions = new WrapPanel();
        trendOptions.Children.Add(Text("Compare with  ", 11)); trendOptions.Children.Add(electricalTrendMode);
        electricalTrendBin.Margin = new Thickness(12, 0, 0, 0); trendOptions.Children.Add(electricalTrendBin);
        var trendPanel = Panel(Vertical(Header("Electrical degradation trend"), trendOptions, electricalTrend, electricalTrendSummary,
            Text("Up = lower voltage at comparable load. Shading = observed 5th–95th percentile spread, not a confidence interval.\nThis comparison does not establish connector damage. Uses the time range above; history is retained up to 24 hours.", 10)));
        ElectricalTrendPreview = trendPanel;
        trendPanel.Margin = new Thickness(0, 0, 0, 16); content.Children.Add(trendPanel);
        var middle = new Grid { Margin = new Thickness(0, 0, 0, 16) }; middle.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.3, GridUnitType.Star) }); middle.ColumnDefinitions.Add(new ColumnDefinition());
        bins.Items.Add("Current load bin"); bins.Items.Add("All loads (descriptive)"); bins.SelectedIndex = 0;
        bins.SelectionChanged += (_, _) => { if (!busy) RenderData(); };
        histogramStats.Foreground = Palette.Muted;
        var dist = Panel(Vertical(Header("Voltage distribution", bins), histogram, histogramStats)); dist.Margin = new Thickness(0, 0, 8, 0); middle.Children.Add(dist);
        analysisBody.Foreground = Palette.Muted; analysisBody.LineHeight = 20;
        var reference = Panel(Vertical(Header("Reference & analysis"), analysisTitle, progress, analysisBody)); reference.Margin = new Thickness(8, 0, 0, 0); Grid.SetColumn(reference, 1); middle.Children.Add(reference); content.Children.Add(middle);
        var ack = Button("Acknowledge selected"); ack.Click += (_, _) => Acknowledge();
        var more = Button("View all"); more.Click += (_, _) => { allWarnings = !allWarnings; more.Content = allWarnings ? "Show recent" : "View all"; warnings.Height = allWarnings ? 220 : 76; RenderData(); };
        var warningActions = new StackPanel { Orientation = Orientation.Horizontal }; warningActions.Children.Add(more); warningActions.Children.Add(ack);
        var warningPanel = Panel(Vertical(Header("Recent warnings", warningActions), warnings)); content.Children.Add(warningPanel);
        warnings.MouseDoubleClick += (_, _) => FocusWarning(); warnings.KeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Enter) FocusWarning(); };
        var actions = new WrapPanel { Margin = new Thickness(0, 16, 0, 12) };
        var details = Button("Sensor details"); details.Click += (_, _) => ShowDetails();
        var settingsButton = Button("Settings"); settingsButton.Click += (_, _) => ShowSettings();
        var approvalsButton = Button("Refresh driver approval");
        approvalsButton.Click += async (_, _) =>
        {
            if (demo) return;
            await client.Send("hello");
            approvalCheckBeforeRefresh = store.Current?.ApprovalCheckedUtc;
            var reply = await client.Send("refresh-driver-approvals");
            awaitingApprovalRefresh = reply is not null;
            operation = reply?.Detail ?? "Could not contact the monitor. Start monitoring and try again.";
            UpdateStatus();
        };
        updateButton.Click += async (_, _) => await CheckForAppUpdate(true);
        var logs = Button("Open logs"); logs.Click += (_, _) => OpenPath(data);
        var export = Button("Export visible CSV"); export.Click += (_, _) => Export();
        resumeLive.Visibility = Visibility.Collapsed; resumeLive.Click += (_, _) => { focusTime = null; RenderData(); };
        foreach (var b in new[] { details, settingsButton, approvalsButton, updateButton, logs, export, resumeLive }) actions.Children.Add(b);
        footer.Foreground = Palette.Muted;
        var shell = new DockPanel { Background = Palette.Bg };
        var bottom = new Border { Background = Palette.Bg, Padding = new Thickness(24, 0, 24, 12), Child = Vertical(actions, footer) }; DockPanel.SetDock(bottom, Dock.Bottom); shell.Children.Add(bottom);
        shell.Children.Add(new ScrollViewer { Content = content, Background = Palette.Bg, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled });
        Content = shell;
    }
    void CreateTray()
    {
        trayMenu = new Forms.ContextMenuStrip();
        trayMenu.Items.Add("Open dashboard", null, (_, _) => Dispatcher.Invoke(Restore));
        trayMenu.Items.Add("Open logs", null, (_, _) => Dispatcher.Invoke(() => OpenPath(data)));
        trayMenu.Items.Add(new Forms.ToolStripSeparator());
        trayMenu.Items.Add("Exit GUI — keep monitoring", null, async (_, _) => await ExitGui());
        trayMenu.Items.Add("Stop monitoring and exit", null, async (_, _) => await StopAndExit());
        tray = new Forms.NotifyIcon { Icon = Drawing.SystemIcons.Information, Text = "ConnectorWatch · connecting", Visible = true, ContextMenuStrip = trayMenu };
        tray.DoubleClick += (_, _) => Dispatcher.Invoke(Restore);
    }
    public async Task Initialize()
    {
        if (demo) FillDemo();
        else
        {
            connected = await client.Send("hello") != null;
            if (!connected && !noStart)
            {
                Directory.CreateDirectory(data);
                bool unlocked;
                try { using var l = new FileStream(Path.Combine(data, "monitor.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); unlocked = true; }
                catch (IOException) { unlocked = false; }
                if (unlocked)
                {
                    string daemon = Path.Combine(AppContext.BaseDirectory, "ConnectorWatch.exe");
                    if (!File.Exists(daemon)) operation = "Daemon executable missing beside the GUI. Use the complete published package.";
                    else
                    {
                        var start = new ProcessStartInfo(daemon) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = AppContext.BaseDirectory };
                        start.ArgumentList.Add("--config"); start.ArgumentList.Add(configPath);
                        Process.Start(start)?.Dispose();
                        operation = "Starting monitor…";
                        for (int i = 0; i < 10; i++) { await Task.Delay(250); if (await client.Send("hello") != null) { connected = true; operation = ""; break; } }
                    }
                }
                else operation = "Existing monitor is running without a compatible control endpoint. Viewing only.";
            }
        }
        initialized = true; await Refresh(); if (!render) timer.Start();
        if (!demo && !render && !QuietTest) _ = CheckForAppUpdate(false);
    }
    async Task Refresh()
    {
        if (busy || exiting) return; busy = true;
        long refreshStarted = Stopwatch.GetTimestamp();
        double? refreshGapSeconds = lastRefreshStarted == 0 ? null : Stopwatch.GetElapsedTime(lastRefreshStarted, refreshStarted).TotalSeconds;
        lastRefreshStarted = refreshStarted;
        try
        {
            if (!demo)
            {
                connected = await client.Send("hello") != null;
                if (connected) await client.Send("lease");
                await store.RefreshAsync();
                if (awaitingApprovalRefresh && store.Current?.ApprovalCheckedUtc != approvalCheckBeforeRefresh)
                { awaitingApprovalRefresh = false; operation = ""; }
                QueueConfidenceRefresh();
                if (store.ReadError.Length > 0) GuiLog.Current.Write("telemetry_read_error", new { data, store.ReadError }, throttle: true);
            }
            var s = store.Current; bool fresh = s?.Fresh(config.MaxAgeSeconds) == true;
            if (firstPoll) { foreach (var w in store.Incidents) notified.Add(w.Id); firstPoll = false; }
            if (fresh) {
                if (lossNotified) GuiLog.Current.Write("telemetry_recovered", new { data, snapshot_time = s!.Time, connected });
                hadFresh = true; lossNotified = false;
            }
            if (!fresh && (hadFresh || s != null) && !lossNotified)
            {
                GuiLog.Current.Write("telemetry_lost", new { data, snapshot_time = s?.Time, sample_age_seconds = s == null ? (double?)null : (DateTimeOffset.UtcNow - s.Time).TotalSeconds, config.MaxAgeSeconds, stopped = s?.Stopped, connected, daemon_pid = client.Identity?.Pid, store.ReadError, refreshGapSeconds, refresh_duration_seconds = Stopwatch.GetElapsedTime(refreshStarted).TotalSeconds });
                lossNotified = true; var id = DateTimeOffset.UtcNow.ToString("O") + "|MONITOR_STOPPED";
                var incident = new Incident(id, DateTimeOffset.UtcNow, "MONITOR_STOPPED", "Monitoring stopped or output is stale. Last values are historical.");
                store.Incidents.Add(incident); localIncidents.Add(incident);
                SaveSettings();
                Notify("Monitoring unavailable", "ConnectorWatch is no longer receiving fresh telemetry. Open the dashboard.", true);
            }
            foreach (var warning in store.Incidents.Where(w => !notified.Contains(w.Id)).ToArray())
            {
                notified.Add(warning.Id);
                if ((DateTimeOffset.UtcNow - warning.Time).TotalSeconds < 30 && warning.Status != "MONITOR_STOPPED") Notify(Friendly(warning.Status), warning.Detail.Length > 0 ? warning.Detail : "A voltage trend warning needs attention. Open the dashboard.", false);
            }
            busy = false;
            UpdateStatus(); if (IsVisible || render) RenderData();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { GuiLog.Current.Write("refresh_error", new { data }, ex, throttle: true); operation = ex.Message; UpdateStatus(); }
        finally { busy = false; }
    }
    void UpdateStatus()
    {
        var s = store.Current; bool fresh = s?.Fresh(config.MaxAgeSeconds) == true;
        double age = s == null ? 0 : Math.Max(0, (DateTimeOffset.UtcNow - s.Time).TotalSeconds);
        string state = fresh ? (demo ? "Synthetic preview" : "Monitoring") : s?.Stopped == true ? "Monitor stopped" : "No fresh telemetry";
        connection.Text = state + (s == null ? "" : $"  ·  {age:F0}s since update");
        connection.Foreground = fresh ? Palette.Cyan : Palette.Amber;
        bool pathValues = s?.ElectricalStatus is "AVAILABLE" or "UNVERIFIED";
        double? connectorVoltage = s?.ConnectorVoltage ?? s?.Voltage;
        double? connectorPower = s?.ConnectorPower ?? (s?.ElectricalSource.Length == 0 ? s?.Power : null);
        double? pcieVoltage = s?.PcieVoltage ?? s?.Pcie;
        voltage.Text = fresh && pathValues ? FormatValue(connectorVoltage, "V", 3) : "—";
        pcie.Text = fresh && pathValues ? FormatValue(pcieVoltage, "V", 3) : "—";
        power.Text = fresh && pathValues ? FormatValue(connectorPower, "W", 1) : "—";
        bool direct = demo || s?.ConnectorPower.HasValue == true || s?.Source.StartsWith("direct NVIDIA", StringComparison.OrdinalIgnoreCase) == true;
        powerLabel.Text = direct ? "CONNECTOR POWER" : "COMPARISON POWER";
        powerNote.Text = s?.ConnectorPower.HasValue == true ? "Typed connector rail power" : direct ? "Legacy source; provenance unverified" : "Power supplied by voltage source";
        change.Text = fresh && s?.Drop is double dr ? $"{-dr * 1000:+0;−0;0} mV" : "—";
        referenceNote.Text = s?.Reference is double r ? $"Reference {r:F3} V" : "Awaiting a learned reference";
        bool alert = fresh && (s?.Status is "SUDDEN_DROOP" or "BASELINE_SHIFT" || s?.ResidualDetectorAlert == true || s?.ActiveIncidentCount > 0);
        string message = StatusMessage(s, fresh, config.MinAnalysisWatts, operation, store.ReadError);
        if (demo) message = "Synthetic demonstration — charts and warnings below are test data.";
        bannerText.Text = message; bannerText.Foreground = alert ? Palette.Red : !fresh || QualitySummary(s).Length > 0 || store.ReadError.Length > 0 ? Palette.Amber : Palette.Muted;
        banner.Background = Palette.Panel; banner.BorderBrush = alert ? Palette.Red : Palette.Line; banner.BorderThickness = new Thickness(1);
        powerPath.Text = $"Source path  {SourceLabel(s)}\n16-pin  {FormatValue(s?.ConnectorVoltage ?? s?.Voltage, "V", 3)}  ·  {FormatValue(s?.ConnectorCurrent ?? s?.Current, "A", 2)}  ·  {FormatValue(s?.ConnectorPower ?? (s?.ElectricalSource.Length == 0 ? s?.Power : null), "W", 1)}\nPCIe  {FormatValue(s?.PcieVoltage ?? s?.Pcie, "V", 3)}  ·  {FormatValue(s?.PcieCurrent ?? s?.PcieCurrent, "A", 2)}  ·  {FormatValue(s?.PciePower, "W", 1)}\nSelected analysis load  {FormatValue(s?.AnalysisLoadValue, s?.AnalysisLoadUnit, 1)}  ·  {SourceLabel(s?.AnalysisLoadSource)}  ·  {s?.AnalysisLoadStatus ?? "UNAVAILABLE"}";
        degradation.Text = QualityDetails(s) + (string.IsNullOrEmpty(s?.ApprovalState) ? "" :
            $"\nDriver {s.DriverVersion} · catalog {s.CatalogRevision?.ToString() ?? "unavailable"} · {s.ApprovalDetail}" +
            $"\nLast approval check: {s.ApprovalCheckedUtc?.ToLocalTime().ToString("g") ?? "not checked"}" +
            (s.DriverUnvalidated ? " · UNVALIDATED DEVELOPER SESSION" : ""));
        if (tray != null)
        {
            tray.Icon = !fresh ? Drawing.SystemIcons.Error : alert ? Drawing.SystemIcons.Warning : Drawing.SystemIcons.Information;
            string text = $"ConnectorWatch · {state}\n{FormatValue(connectorVoltage, "V", 3)} · {age:F0}s ago"; tray.Text = text.Length > 63 ? text[..63] : text;
        }
    }
    static string FormatValue(double? value, string? unit, int decimals) => value is double number && double.IsFinite(number) ? number.ToString($"F{decimals}", CultureInfo.InvariantCulture) + (string.IsNullOrWhiteSpace(unit) ? "" : " " + unit) : "—";
    static string SourceLabel(Snapshot? s) => SourceLabel(s?.ElectricalSource ?? "");
    static string SourceLabel(string? source) => string.IsNullOrWhiteSpace(source) ? "Unavailable" : source;
    internal static string QualitySummary(Snapshot? s)
    {
        if (s == null) return "";
        var states = new List<string>();
        if (s.ElectricalStatus is "STALE" or "UNAVAILABLE") states.Add("electrical " + s.ElectricalStatus.ToLowerInvariant());
        if (s.AnalysisLoadStatus is "STALE" or "UNAVAILABLE") states.Add("analysis load " + s.AnalysisLoadStatus.ToLowerInvariant());
        if (s.AcquisitionStatus.Length > 0 && s.AcquisitionStatus is not ("HEALTHY" or "SENSOR_UNCHARACTERIZED")) states.Add("acquisition " + Friendly(s.AcquisitionStatus));
        return string.Join("; ", states);
    }
    internal static string StatusMessage(Snapshot? s, bool fresh, int minWatts, string operation = "", string readError = "")
    {
        if (!fresh || s == null) return "Monitoring unavailable. Values shown in history are past observations.";
        string alert = s.Status is "SUDDEN_DROOP" or "BASELINE_SHIFT" ? Friendly(s.Status) :
            s.ResidualDetectorAlert ? Friendly(s.ResidualDetectorStatus) : s.ActiveIncidentCount > 0 ? $"{s.ActiveIncidentCount} active incident(s) need attention" : "";
        if (alert.Length > 0) return alert + ". Save your work and reduce GPU load while investigating.";
        if (s.Status is "DRIVER_AWAITING_APPROVAL" or "DRIVER_REVOKED" or "DRIVER_APPROVAL_UNAVAILABLE" or "APP_UPDATE_REQUIRED")
            return (s.ApprovalDetail.Length > 0 ? s.ApprovalDetail : Friendly(s.Status)) + " Public GPU telemetry remains available.";
        if (operation.Length > 0) return operation;
        if (readError.Length > 0) return "Data read issue: " + readError;
        string quality = QualitySummary(s);
        if (quality.Length > 0) return "Telemetry degraded: " + quality + ".";
        var notes = new List<string>();
        if (s.ElectricalStatus == "UNVERIFIED" || s.AnalysisLoadStatus == "UNVERIFIED") notes.Add("Sensor update timing is unverified.");
        if (s.AcquisitionStatus == "SENSOR_UNCHARACTERIZED") notes.Add("Sensor response has not been characterized.");
        if (s.DifferentialModelState is "" or "UNAVAILABLE") notes.Add("Differential analysis unavailable: no model loaded.");
        else if (!s.DifferentialModelFitted) notes.Add("Differential model is not fitted yet.");
        if (s.ReferenceCompatibility == "LEGACY") notes.Add("Saved reference is unverified; analysis needs an accepted compatible reference.");
        string activity = s.Status == "OUTSIDE_ANALYSIS_RANGE" ? $"Waiting for steady connector load above {minWatts} W." : Friendly(s.Status) + ".";
        return "Receiving telemetry. " + activity + (notes.Count > 0 ? " " + string.Join(" ", notes) : " Aggregate rail readings do not certify connector safety.");
    }
    static string QualityDetails(Snapshot? s)
    {
        if (s == null) return "No snapshot. Values are unavailable.";
        string model = s.DifferentialModelState.Length == 0 ? "UNAVAILABLE" : s.DifferentialModelState;
        string incidents = s.IncidentCount == 0 ? "No incident ledger entries reported" : $"Incidents {s.ActiveIncidentCount} active / {s.IncidentCount} total" + (s.LatestIncidentState.Length > 0 ? $" · latest {s.LatestIncidentState} {s.LatestIncidentStatus}" : "");
        string watchdog = s.WatchdogAvailable ? $"Read-only power-limit monitor {s.WatchdogStatus}; safety certification absent" : "Read-only power-limit monitor unavailable";
        return $"Electrical {s.ElectricalStatus}; freshness {s.ElectricalFreshnessKind}; reference {SourceLabel(s.ReferenceState)}; model {model}; {incidents}; {watchdog}.";
    }
    int? SelectedBin()
    {
        if (bins.SelectedIndex == 1) return null;
        if (bins.SelectedItem is BinChoice b) return b.Value;
        return store.Current?.Bin;
    }
    void QueueConfidenceRefresh()
    {
        if (confidenceHistory == null || confidenceReadBusy || (DateTimeOffset.UtcNow - confidenceLastRead).TotalMinutes < 5) return;
        confidenceReadBusy = true; confidenceLastRead = DateTimeOffset.UtcNow;
        _ = Task.Run(async () =>
        {
            try { await confidenceHistory.RefreshAsync(DateTimeOffset.UtcNow); }
            catch (Exception ex) { GuiLog.Current.Write("confidence_history_error", new { data }, ex, throttle: true); }
            finally
            {
                if (!exiting) await Dispatcher.InvokeAsync(() => { confidenceReadBusy = false; RenderConfidence(); });
            }
        });
    }
    void RenderConfidence()
    {
        if (renderingConfidence) return;
        renderingConfidence = true;
        try
        {
            var now = DateTimeOffset.UtcNow;
            var days = demo ? (IReadOnlyList<ConfidenceDay>)confidenceDemoDays : confidenceHistory?.Days ?? Array.Empty<ConfidenceDay>();
            var selection = (settings.ConfidenceCohort, settings.ConfidenceRangeDays, now.UtcDateTime.Date, confidenceReadBusy, config.ShiftVolts);
            if (ReferenceEquals(days, confidenceRenderedDays) && selection == confidenceRenderedSelection) return;
            var groups = days.Where(d => !string.IsNullOrWhiteSpace(d.Cohort)).GroupBy(d => d.Cohort).ToArray();
            foreach (var group in groups)
            {
                if (confidenceCohort.Items.OfType<ConfidenceCohortChoice>().Any(c => c.Key == group.Key)) continue;
                var last = group.OrderBy(d => d.Day).Last();
                confidenceCohort.Items.Add(new ConfidenceCohortChoice(group.Key, $"{last.MinLoad:F0}–{last.MaxLoad:F0} W · {last.Day:dd MMM}"));
            }
            if (string.IsNullOrWhiteSpace(settings.ConfidenceCohort))
            {
                settings.ConfidenceCohort = groups.Where(g => g.Any(d => DegradationConfidence.SupportsDay(d, g.Key)))
                    .OrderBy(g => g.Min(d => d.Day)).ThenByDescending(g => g.Sum(d => d.MinuteCount)).FirstOrDefault()?.Key ?? "";
                if (settings.ConfidenceCohort.Length > 0 && !demo) SaveSettings();
            }
            confidenceCohort.SelectedItem = confidenceCohort.Items.OfType<ConfidenceCohortChoice>().FirstOrDefault(c => c.Key == settings.ConfidenceCohort) ?? (object)"Automatic comparison";
            var end = new DateTimeOffset(now.UtcDateTime.Date, TimeSpan.Zero);
            var from = end.AddDays(-settings.ConfidenceRangeDays);
            bool confidenceConfigurationError = false;
            IReadOnlyList<ConfidencePoint> points;
            if (string.IsNullOrWhiteSpace(settings.ConfidenceCohort)) points = Array.Empty<ConfidencePoint>();
            else
            {
                try { points = DegradationConfidence.Build(days, settings.ConfidenceCohort, now, config.ShiftVolts); }
                catch (ArgumentOutOfRangeException) { points = Array.Empty<ConfidencePoint>(); confidenceConfigurationError = true; }
            }
            confidencePlot.From = from; confidencePlot.To = end;
            confidencePlot.Points = points.Where(p => p.Day >= from && p.Day < end).ToArray();
            var latest = points.LastOrDefault();
            if (confidenceConfigurationError)
            {
                confidenceValue.Text = "Unavailable";
                confidenceValue.Foreground = Palette.Amber;
                confidenceCaption.Text = "Confidence unavailable: configure a finite positive voltage-shift threshold.";
            }
            else if (latest?.Score is double score)
            {
                confidenceValue.Text = $"{score:F0}%";
                confidenceValue.Foreground = score >= 70 ? Palette.Red : score >= 35 ? Palette.Amber : Palette.Cyan;
                string level = score >= 70 ? "Strong evidence" : score >= 35 ? "Some evidence" : "Little evidence";
                confidenceCaption.Text = $"{level} of sustained voltage decline · through {latest.Day:dd MMM} · {latest.EvidenceDays} comparable days this week";
            }
            else
            {
                confidenceValue.Text = confidenceReadBusy && days.Count == 0 ? "Reading history…" : points.Any(p => p.Score.HasValue) ? "No recent comparison" : "Learning";
                confidenceValue.Foreground = Palette.Muted;
                confidenceCaption.Text = "Needs three comparable days. Missing measurements do not mean low degradation confidence.";
            }
            confidencePlot.EmptyText = confidenceConfigurationError ? "Confidence unavailable: configure a finite positive voltage-shift threshold." : confidenceReadBusy && days.Count == 0 ? "Reading recorded history…" : "Learning — needs three comparable days.";
            confidenceDetails.Text = "Approximate load matching. Each day needs 10 sampled minutes across at least 30 minutes.\nConfidence combines the size, persistence and number of comparable days in the last week.\nThe voltage-drop threshold is " + (config.ShiftVolts * 1000).ToString("F0", CultureInfo.InvariantCulture) + " mV; this policy is not a calibrated failure probability. Sensor freshness may remain unverified. These readings cannot isolate the cause.\n" + (demo ? "Synthetic demonstration." : confidenceHistory?.Status ?? "Waiting for recorded data.");
            if (confidenceConfigurationError)
                confidenceDetails.Text = "Confidence unavailable because ShiftVolts is not finite and positive. The confidence score is an operational evidence summary, not a calibrated failure probability.";
            confidenceDetails.ToolTip = settings.ConfidenceCohort;
            confidenceRenderedDays = days;
            confidenceRenderedSelection = (settings.ConfidenceCohort, settings.ConfidenceRangeDays, now.UtcDateTime.Date, confidenceReadBusy, config.ShiftVolts);
            confidencePlot.InvalidateVisual();
        }
        finally { renderingConfidence = false; }
    }
    void RenderElectricalTrend(DateTimeOffset from, DateTimeOffset to)
    {
        int? chosen = (electricalTrendBin.SelectedItem as BinChoice)?.Value;
        bool initial = electricalTrendMode.SelectedIndex == 0;
        var selection = (settings.RangeMinutes, focusTime, chosen, initial);
        // Minute aggregates need not be rebuilt on every one-second UI tick.
        if (trendSelection == selection && (DateTimeOffset.UtcNow - trendBuiltAt).TotalSeconds < 10) return;
        trendSelection = selection; trendBuiltAt = DateTimeOffset.UtcNow;
        var candidates = store.Samples.Where(p => p.Time >= from && p.Time <= to && p.Eligible).ToArray();
        foreach (int value in candidates.Where(p => p.Bin.HasValue).Select(p => p.Bin!.Value).Distinct().Order())
            if (!electricalTrendBin.Items.OfType<BinChoice>().Any(b => b.Value == value)) electricalTrendBin.Items.Add(new BinChoice(value, config.BinWatts));
        chosen ??= candidates.Where(p => p.Bin.HasValue && (initial || p.Reference.HasValue)).GroupBy(p => p.Bin)
            .OrderByDescending(g => g.Count()).ThenBy(g => g.Key).FirstOrDefault()?.Key;
        var result = ElectricalTrend.Build(store.Samples, from, to, chosen, Math.Max(config.MaxAgeSeconds, config.SampleSeconds * 2), initial);
        electricalTrend.From = from; electricalTrend.To = to;
        electricalTrend.Points = result.Points.Select(p => new TrendPlotPoint(p.Time, p.MedianDropMv, p.P05DropMv, p.P95DropMv,
            p.SegmentId, p.Count, $"{p.Count} unique observations over {p.SpanSeconds:F1}s\nLoad {p.LoadMin:F1}–{p.LoadMax:F1} W\nComparison {p.Reference:F4} V\n" +
            (p.Unverified ? "Source timing or reference is unverified; count does not imply independent measurements." : "Source-timestamp metadata recorded; count does not imply independent measurements."))).ToArray();
        electricalTrend.EmptyText = result.Reason switch {
            "NO_DATA" or "NO_LOAD_BIN" => "No comparable loaded observations in this range.",
            "REFERENCE_UNAVAILABLE" => "No recorded reference. Choose Initial observation to compare recorded voltages.",
            "SETTLING_OR_UNAVAILABLE" => "No settled, available readings for this load band.",
            _ => "Need at least five comparable observations per interval."
        };
        var latest = result.Points.LastOrDefault(p => p.Supported);
        string band = chosen.HasValue ? $"{chosen}–{chosen + config.BinWatts} W load band" : "No eligible load band";
        string comparison = initial ? result.AnchorTime.HasValue ? $"Initial observation at {result.AnchorTime.Value.LocalDateTime:HH:mm:ss}; zero is the starting observation" : "Waiting for an initial observation interval" : "Recorded reference; historical reference acceptance is not verified by CSV";
        electricalTrendSummary.Text = band + " · " + comparison + "\n" +
            (latest == null ? electricalTrend.EmptyText : $"Latest interval from {latest.Start.LocalDateTime:HH:mm}: {latest.MedianDropMv:+0.0;-0.0;0.0} mV drop · {latest.Count} observations · {latest.SpanSeconds:F1}s sampled span") +
            "\nMost recent source context only. One-minute intervals; gaps are disconnected. Source-timing and legacy-reference limitations remain visible in hover details.";
        electricalTrendSummary.ToolTip = "Comparison context (GPU, electrical source, load source, unit):\n" + result.ContextIdentity.Replace(" | ", "\n");
        electricalTrend.InvalidateVisual();
    }
    void RenderData()
    {
        if (!initialized || busy) return;
        var s = store.Current;
        foreach (var b in store.Samples.Where(p => p.Eligible).Select(p => p.Bin!.Value).Concat(store.Baselines.Keys).Distinct().Order())
            if (!bins.Items.OfType<BinChoice>().Any(x => x.Value == b)) bins.Items.Add(new BinChoice(b, config.BinWatts));
        var end = focusTime?.AddMinutes(2) ?? DateTimeOffset.UtcNow;
        var from = end.AddMinutes(-settings.RangeMinutes);
        int? selected = SelectedBin(); bool all = bins.SelectedIndex == 1;
        visiblePoints = PlotData.Select(store.Samples, from, end, all ? null : selected, false);
        // Current bin is deliberately empty at idle; All loads remains an explicit descriptive view.
        if (!all && !selected.HasValue) visiblePoints = PlotData.Select(store.Samples, from, end, null, false);
        distributionPoints = !all && !selected.HasValue ? new() : PlotData.Select(store.Samples, from, end, selected, !all);
        Baseline? baseline = selected.HasValue && store.Baselines.TryGetValue(selected.Value, out var saved) ? saved : null;
        if (selected == s?.Bin && s?.Reference != null) baseline = new(s.Reference, baseline?.P05, s.Learning);
        history.Samples = visiblePoints; history.From = from; history.To = end; history.ShowPcie = settings.ShowPcie; history.Reference = all || !selected.HasValue ? null : baseline?.Median; history.GapSeconds = Math.Max(config.MaxAgeSeconds, config.SampleSeconds * 2); history.InvalidateVisual();
        histogram.Histogram = PlotData.Histogram(distributionPoints); histogram.Reference = all ? null : baseline;
        histogram.EmptyText = !all && !selected.HasValue ? $"Waiting for steady load above {config.MinAnalysisWatts} W." : "No eligible samples in this range.";
        histogram.InvalidateVisual();
        var h = histogram.Histogram;
        histogramStats.Text = $"{h.Total:N0} unique samples · {h.Width:F2} V buckets" + (selected.HasValue ? $" · {selected}–{selected + config.BinWatts} W" : "") + "\n" + (h.Total > 0 ? $"Median {h.Median:F3} V   P05 {h.P05:F3} V" : "Choose a historical load bin or All loads.") + (all ? "\nAll loads: descriptive distribution only." : "\nAmber: reference median · Violet: reference P05");
        RenderElectricalTrend(from, end);
        RenderConfidence();
        analysisTitle.Text = s == null || !s.Fresh(config.MaxAgeSeconds) ? "Monitoring unavailable" : Friendly(s.Status);
        progress.Maximum = config.BaselineSamples; progress.Value = s?.Reference.HasValue == true ? config.BaselineSamples : Math.Min(config.BaselineSamples, s?.Learning ?? 0);
        string binLabel = s?.Bin is int bnow ? $"{bnow}–{bnow + config.BinWatts} W connector load" : "No eligible load bin";
        string referenceText = s?.Reference.HasValue == true ? $"Frozen reference  {FormatValue(s.Reference, "V", 3)}\nCurrent median  {FormatValue(s.Median, "V", 3)}  ·  P05 {FormatValue(s.P05, "V", 3)}\nWindow  {s.Window} / {config.WindowSamples} eligible samples" : $"Reference learning  {s?.Learning ?? 0} / {config.BaselineSamples} samples\nLearning accumulates during steady load.";
        string modelText = $"Selected load  {FormatValue(s?.AnalysisLoadValue, s?.AnalysisLoadUnit, 1)} · {SourceLabel(s?.AnalysisLoadSource)} · {s?.AnalysisLoadStatus ?? "UNAVAILABLE"}\nReference lifecycle  {SourceLabel(s?.ReferenceState)}" + (s?.ReferenceCompatibility.Length > 0 ? $" · {s.ReferenceCompatibility}" : "") + $"\nDifferential model  {SourceLabel(s?.DifferentialModelState)} · slope {FormatValue(s?.DifferentialModelSlope, "V/unit", 5)}\nPrediction  expected {FormatValue(s?.ExpectedVoltage, "V", 3)} · observed {FormatValue(s?.ObservedVoltage, "V", 3)} · residual {FormatValue(s?.Residual, "V", 3)}\nResidual detector  {SourceLabel(s?.ResidualDetectorStatus)}";
        string incidentText = s == null || s.IncidentCount == 0 ? "Incidents  0 active / 0 total" : $"Incidents  {s.ActiveIncidentCount} active / {s.IncidentCount} total · latest {SourceLabel(s.LatestIncidentState)} {SourceLabel(s.LatestIncidentStatus)}";
        string watchdogText = s?.WatchdogAvailable == true ? $"Power-limit monitor  READ-ONLY · {SourceLabel(s.WatchdogStatus)}" : "Power-limit monitor  READ-ONLY · UNAVAILABLE";
        analysisBody.Text = binLabel + "\n" + referenceText + $"\nShift threshold  {config.ShiftVolts * 1000:F0} mV  ·  sudden {config.SuddenDroopVolts * 1000:F0} mV\n" + modelText + $"\n{incidentText}\n{watchdogText}";
        if (renderedWarningRevision != store.Incidents.Revision || renderedAcknowledgementRevision != acknowledged.Revision || renderedAllWarnings != allWarnings)
        {
            string? chosen = (warnings.SelectedItem as ListBoxItem)?.Tag as string;
            warnings.Items.Clear();
            foreach (var w in store.Incidents.OrderByDescending(w => w.Time).Take(allWarnings ? 2000 : 20))
            {
                string text = $"{w.Time.LocalDateTime:HH:mm:ss}   {Friendly(w.Status)}" + (w.Voltage.HasValue ? $"   {w.Voltage:F3} V" : "") + (w.Drop.HasValue ? $"   {w.Drop * 1000:F0} mV drop" : "") + (acknowledged.Contains(w.Id) ? "   · read" : "   · unread");
                var item = new ListBoxItem { Content = Text(text, 12), Tag = w.Id, ToolTip = w.Detail, Padding = new Thickness(4, 5, 4, 5), Foreground = Palette.Text };
                warnings.Items.Add(item); if (w.Id == chosen) item.IsSelected = true;
            }
            if (warnings.Items.Count == 0) warnings.Items.Add(new ListBoxItem { Content = Text("No recorded warnings in the loaded history.", 12), IsEnabled = false });
            renderedWarningRevision = store.Incidents.Revision; renderedAcknowledgementRevision = acknowledged.Revision; renderedAllWarnings = allWarnings;
        }
        resumeLive.Visibility = focusTime.HasValue ? Visibility.Visible : Visibility.Collapsed;
        footer.Text = (demo ? "Preview only" : connected ? "Connected to daemon" : "Read-only view · control endpoint unavailable") + "  ·  " + (store.Limited ? "History limited to newest loaded samples (200,000 max)." : "History: newest 24 hours.") + "\nVoltage trends cannot guarantee warning before connector damage. Close this window to keep monitoring in the tray.";
    }
    void Notify(string title, string body, bool urgent)
    {
        if (demo || render || QuietTest || tray == null || (!urgent && (DateTimeOffset.UtcNow - lastNotification).TotalSeconds < 10)) return;
        lastNotification = DateTimeOffset.UtcNow;
        tray.ShowBalloonTip(10000, title, body.Length > 240 ? body[..240] : body, Forms.ToolTipIcon.Warning);
    }
    void Acknowledge()
    {
        if ((warnings.SelectedItem as ListBoxItem)?.Tag is string id) { acknowledged.Add(id); SaveSettings(); RenderData(); }
    }
    void FocusWarning()
    {
        if ((warnings.SelectedItem as ListBoxItem)?.Tag is string id && store.Incidents.FirstOrDefault(w => w.Id == id) is Incident w)
        { focusTime = w.Time; bins.SelectedIndex = w.Bin.HasValue ? Math.Max(0, bins.Items.IndexOf(bins.Items.OfType<BinChoice>().FirstOrDefault(b => b.Value == w.Bin))) : 1; RenderData(); }
    }
    void ShowDetails()
    {
        var s = store.Current;
        string gpu = string.IsNullOrWhiteSpace(s?.GpuUuid) ? (string.IsNullOrWhiteSpace(config.GpuUuid) ? "Automatic (awaiting daemon)" : config.GpuUuid) : s.GpuUuid;
        string time = s == null || s.Time == default ? "—" : s.Time.LocalDateTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        string incidents = s == null ? "—" : $"{s.ActiveIncidentCount} active / {s.IncidentCount} total" + (s.LatestIncidentState.Length > 0 ? $" · latest {s.LatestIncidentState} {s.LatestIncidentStatus}" : "");
        string watchdog = s?.WatchdogAvailable == true ? $"{s.WatchdogStatus} · board {SourceLabel(s.WatchdogBoardStatus)} · read-only {Flag(true, s.WatchdogReadOnly)} · no safety certification {Flag(true, s.WatchdogNoSafetyCertification)}" : "Unavailable";
        ShowText("Sensor details", $"GPU UUID\n{gpu}\n\nSource path\n{SourceLabel(s)}\nElectrical state\n{s?.ElectricalStatus ?? "UNAVAILABLE"} · freshness {s?.ElectricalFreshnessKind ?? "Unavailable"}\n16-pin  {FormatValue(s?.ConnectorVoltage ?? s?.Voltage, "V", 6)}  ·  {FormatValue(s?.ConnectorCurrent ?? s?.Current, "A", 3)}  ·  {FormatValue(s?.ConnectorPower ?? (s?.ElectricalSource.Length == 0 ? s?.Power : null), "W", 3)}\nPCIe  {FormatValue(s?.PcieVoltage ?? s?.Pcie, "V", 6)}  ·  {FormatValue(s?.PcieCurrent, "A", 3)}  ·  {FormatValue(s?.PciePower, "W", 3)}\nSelected analysis load  {FormatValue(s?.AnalysisLoadValue, s?.AnalysisLoadUnit, 3)} · {SourceLabel(s?.AnalysisLoadSource)} · {s?.AnalysisLoadStatus ?? "UNAVAILABLE"}\n\nModel\nReference {SourceLabel(s?.ReferenceState)} · compatibility {SourceLabel(s?.ReferenceCompatibility)}\nExpected {FormatValue(s?.ExpectedVoltage, "V", 6)} · observed {FormatValue(s?.ObservedVoltage, "V", 6)} · residual {FormatValue(s?.Residual, "V", 6)}\nModel slope {FormatValue(s?.DifferentialModelSlope ?? s?.DifferentialSlope, "V/unit", 6)} · detector {SourceLabel(s?.ResidualDetectorStatus)}\n\nIncidents\n{incidents}\n\nPower-limit watchdog\n{watchdog}\nConfigured limit {FormatValue(s?.WatchdogConfiguredLimit, "W", 1)} · observed limit {FormatValue(s?.WatchdogObservedLimit, "W", 1)} · board power {FormatValue(s?.WatchdogBoardPower ?? s?.BoardPower, "W", 1)}\n\nGPU temperature {FormatValue(s?.Temperature, "°C", 0)}  ·  utilization {FormatValue(s?.Utilization, "%", 0)}  ·  power limit {FormatValue(s?.Limit, "W", 0)}\n\nStatus\n{s?.Status ?? "UNAVAILABLE"}  ·  schema {s?.Schema}\n{s?.AcquisitionStatus} {s?.AcquisitionDetail}\n{s?.Detail}\n\nTimestamps are host polling time unless a verified source timestamp is shown. Native sensor freshness may be unverified. GPU temperature is not connector temperature. These readings do not certify connector safety.\n\nData directory\n{data}");
    }
    static string Flag(bool available, bool value) => available ? value ? "yes" : "no" : "—";
    void ShowSettings()
    {
        var body = Vertical(Text($"Sampling  {config.SampleSeconds}s\nMinimum load  {config.MinAnalysisWatts} W\nLoad bins  {config.BinWatts} W\nReference  {config.BaselineSamples} samples\nRolling window  {config.WindowSamples} samples\n\nThreshold and hardware changes require a daemon restart.\nClosing the window hides it to the tray. Explicit Exit GUI keeps monitoring.", 13));
        var startup = new ComboBox { Margin = new Thickness(0, 8, 0, 8) }; startup.Items.Add("GUI in tray at sign-in"); startup.Items.Add("Background monitor only at sign-in"); startup.Items.Add("Disable automatic startup"); startup.SelectedIndex = 0;
        var startupResult = Text("Startup is optional. Apply changes only when you want automatic startup.", 11);
        var applyStartup = Button("Apply startup preference");
        applyStartup.Click += async (_, _) =>
        {
            if (demo) { startupResult.Text = "Startup changes are disabled in the synthetic preview."; return; }
            applyStartup.IsEnabled = false;
            try
            {
                string script = Path.Combine(AppContext.BaseDirectory, "Configure-Startup.ps1");
                if (!File.Exists(script)) throw new IOException("Startup helper is missing from the package.");
                var start = new ProcessStartInfo("powershell.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
                foreach (var arg in new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", script, "-Mode", startup.SelectedIndex == 2 ? "Remove" : startup.SelectedIndex == 1 ? "Monitor" : "Gui", "-ConfigPath", configPath }) start.ArgumentList.Add(arg);
                using var process = Process.Start(start) ?? throw new IOException("Could not start configuration helper.");
                var errors = process.StandardError.ReadToEndAsync(); var output = process.StandardOutput.ReadToEndAsync(); await process.WaitForExitAsync();
                startupResult.Text = process.ExitCode == 0 ? "Startup preference saved for your Windows account." : "Startup was not changed: " + await errors;
                await output;
            }
            catch (Exception ex) when (ex is IOException or Win32Exception) { startupResult.Text = ex.Message; }
            finally { applyStartup.IsEnabled = true; }
        };
        body.Children.Add(startup); body.Children.Add(applyStartup); body.Children.Add(startupResult);
        var open = Button("Open advanced configuration"); open.Margin = new Thickness(0, 20, 0, 0); open.Click += (_, _) => OpenPath(configPath); body.Children.Add(open);
        ShowWindow("Settings", body);
    }
    void ShowText(string title, string text) => ShowWindow(title, new TextBox { Text = text, IsReadOnly = true, TextWrapping = TextWrapping.Wrap, Background = Palette.Panel, Foreground = Palette.Text, BorderThickness = new Thickness(0), VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
    void ShowWindow(string title, UIElement body)
    {
        var w = new Window { Owner = this, Title = title, Width = 590, Height = 530, Background = Palette.Panel, Foreground = Palette.Text, WindowStartupLocation = WindowStartupLocation.CenterOwner, Content = new Border { Padding = new Thickness(24), Child = body } }; w.Show();
    }
    static void OpenPath(string path)
    {
        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
        catch (Exception ex) when (ex is Win32Exception or IOException) { MessageBox.Show(ex.Message, "Could not open", MessageBoxButton.OK, MessageBoxImage.Information); }
    }
    void Export()
    {
        var dialog = new Microsoft.Win32.SaveFileDialog { FileName = "ConnectorWatch-visible.csv", Filter = "CSV files|*.csv" };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            var b = new StringBuilder("timestamp_utc,voltage_v,pcie_voltage_v,comparison_power_w,bin_w,status\n");
            foreach (var p in visiblePoints) b.AppendLine(FormattableString.Invariant($"{p.Time:O},{p.Voltage},{p.Pcie},{p.Power},{p.Bin},{p.Status}"));
            File.WriteAllText(dialog.FileName, b.ToString());
        }
        catch (IOException ex) { ShowText("Export failed", ex.Message); }
    }
    void OnClosing(object? sender, CancelEventArgs e)
    {
        if (exiting) return;
        e.Cancel = true; Hide(); SaveSettings();
        if (!demo && !QuietTest && !settings.CloseTipShown && tray != null)
        { settings.CloseTipShown = true; tray.ShowBalloonTip(4000, "Still monitoring", "ConnectorWatch is in the tray beside the clock. Double-click its icon to return.", Forms.ToolTipIcon.Info); SaveSettings(); }
    }
    public void Restore() { Show(); if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal; Activate(); }
    public void SessionEnding()
    {
        exiting = true; timer.Stop(); SaveSettings(); tray?.Dispose();
        FlushConfidenceHistory(TimeSpan.FromSeconds(2)).GetAwaiter().GetResult();
    }
    async Task FlushConfidenceHistory(TimeSpan timeout)
    {
        if (confidenceHistory is null) return;
        try { await Task.Run(() => confidenceHistory.FlushAsync()).WaitAsync(timeout).ConfigureAwait(false); }
        catch (Exception ex) { GuiLog.Current.Write("confidence_history_shutdown_checkpoint", new { data }, ex, throttle: true); }
    }
    public async Task ExitGui()
    {
        if (exiting) return; exiting = true; timer.Stop();
        updateEnding.Cancel();
        if (!demo) await client.Send("release");
        await FlushConfidenceHistory(TimeSpan.FromSeconds(5));
        SaveSettings(); tray?.Dispose(); trayMenu?.Dispose(); SystemParameters.StaticPropertyChanged -= ThemeChanged; Close(); Application.Current.Shutdown();
    }
    async Task StopAndExit()
    {
        if (demo) { await ExitGui(); return; }
        if (await client.Send("hello") == null || await client.Send("stop") == null) { operation = "Could not request a graceful stop. Monitor may already be stopped or incompatible."; Restore(); UpdateStatus(); return; }
        operation = "Stopping monitor…"; UpdateStatus();
        for (int i = 0; i < 30; i++)
        {
            await Task.Delay(200);
            try { if (Snapshot.Parse(TelemetryStore.ReadShared(Path.Combine(data, "status.json"))).Stopped) { await ExitGui(); return; } } catch (Exception ex) when (ex is IOException or JsonException) { }
        }
        operation = "Stop requested, but shutdown is not confirmed. Check status and logs."; Restore(); UpdateStatus();
    }
    void SaveSettings()
    {
        if (render) return;
        try
        {
            if (WindowState == WindowState.Normal) { settings.Width = Width; settings.Height = Height; settings.Left = Left; settings.Top = Top; }
            settings.Acknowledged = acknowledged.ToArray(); settings.LocalIncidents = localIncidents.ToArray(); Directory.CreateDirectory(Path.GetDirectoryName(settingsPath)!);
            File.WriteAllText(settingsPath + ".tmp", JsonSerializer.Serialize(settings)); File.Move(settingsPath + ".tmp", settingsPath, true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { GuiLog.Current.Write("settings_write_error", new { settingsPath }, ex, throttle: true); operation = "Could not save dashboard preferences: " + ex.Message; }
    }
    void ThemeChanged(object? sender, PropertyChangedEventArgs e) { if (e.PropertyName == nameof(SystemParameters.HighContrast)) { Background = Palette.Bg; Foreground = Palette.Text; history.InvalidateVisual(); histogram.InvalidateVisual(); electricalTrend.InvalidateVisual(); confidencePlot.InvalidateVisual(); } }
    void FillDemo()
    {
        var now = DateTimeOffset.UtcNow; var random = new Random(5090);
        for (int day = 0; day < 45; day++)
        {
            var at = new DateTimeOffset(now.UtcDateTime.Date, TimeSpan.Zero).AddDays(day - 45);
            confidenceDemoDays.Add(new ConfidenceDay {
                Day = at, Cohort = "Synthetic 425–450 W", Epoch = "demo-reference-1", MedianDropMv = Math.Max(0, (day - 22) * 13),
                MinuteCount = 60, ObservationCount = 3600, FirstSample = at.AddHours(12), LastSample = at.AddHours(14),
                MinLoad = 430, MaxLoad = 440, MedianLoad = 435, P10Load = 431, P90Load = 439,
                AnchorMedianLoad = 435, AnchorP10Load = 431, AnchorP90Load = 439, BinWidth = 25, Unverified = true
            });
        }
        for (int i = 0; i < 900; i++)
        {
            var t = now.AddSeconds(i - 899); double v = 12.07 + .008 * Math.Sin(i / 19.0) + random.NextDouble() * .012;
            string state = "NO_SHIFT_DETECTED";
            if (i > 640 && i < 660) { v -= .28; state = "SUDDEN_DROOP"; }
            if (i > 780) { v -= .22; state = "BASELINE_SHIFT"; }
            if (i is > 350 and < 380) continue;
            store.Samples.Add(new(t, t, v, 12.1 + random.NextDouble() * .008, 438 + random.NextDouble() * 5, 425, state, 12.08, 12.06, 12.08 - v, "Synthetic voltage event"));
        }
        var last = store.Samples[^1];
        store.Current = new Snapshot { Time = now, Voltage = last.Voltage, Pcie = last.Pcie, Power = last.Power, ConnectorVoltage = last.Voltage, ConnectorCurrent = 36.5, ConnectorPower = last.Power, PcieVoltage = last.Pcie, PcieCurrent = 4.2, PciePower = 50.4, ElectricalSource = "Synthetic fixture · no hardware", ElectricalStatus = "UNVERIFIED", ElectricalFresh = true, ElectricalFreshnessKind = "Synthetic", AnalysisLoadSource = "CONNECTOR_POWER", AnalysisLoadValue = last.Power, AnalysisLoadUnit = "W", AnalysisLoadAvailable = true, AnalysisLoadFresh = true, AnalysisLoadStatus = "UNVERIFIED", DifferentialModelState = "SYNTHETIC", ResidualDetectorStatus = "SYNTHETIC", BoardPower = 452, Temperature = 62, Utilization = 97, Limit = 450, Bin = 425, Status = "BASELINE_SHIFT", Reference = 12.08, Median = 11.856, P05 = 11.849, Drop = .224, Learning = 300, Window = 60, Schema = 2, Source = "Synthetic fixture · no hardware" };
        store.Baselines[425] = new(12.08, 12.06, 0);
        store.Incidents.Add(new("demo1", now.AddSeconds(-250), "SUDDEN_DROOP", "Synthetic 280 mV drop", 11.80, .28, 425));
        store.Incidents.Add(new("demo2", now.AddSeconds(-100), "BASELINE_SHIFT", "Synthetic sustained change", 11.856, .224, 425));
    }
    public async Task<string[]> RunUiChecks()
    {
        if (!demo) throw new InvalidOperationException("UI tests require synthetic mode");
        var checks = new List<string>();
        void Check(bool passed, string name) { if (!passed) throw new Exception("UI test failed: " + name); checks.Add(name); }
        Check(IsVisible && tray?.Visible == true, "Dashboard and notification icon start visible");
        Check(confidencePlot.Points.Count == 30 && confidencePlot.Points.Any(p => p.Score == 0) && confidencePlot.Points.Any(p => p.Score >= 90), "Long-term confidence uses daily points and shows increasing synthetic evidence");
        double savedConfidenceShift = config.ShiftVolts;
        config.ShiftVolts = 0;
        RenderConfidence();
        Check(confidenceValue.Text == "Unavailable" && confidencePlot.Points.Count == 0 && confidenceCaption.Text.Contains("configure a finite positive", StringComparison.Ordinal), "Invalid confidence threshold leaves the dashboard running with a clear unavailable state");
        config.ShiftVolts = savedConfidenceShift;
        RenderConfidence();
        Check(confidencePlot.Points.Count == 30 && confidencePlot.Points.Any(p => p.Score >= 90), "Valid confidence threshold restores daily evidence rendering");
        var incompleteConfidenceDay = confidenceDemoDays[0] with { P10Load = null };
        Check(!DegradationConfidence.SupportsDay(incompleteConfidenceDay, incompleteConfidenceDay.Cohort), "Automatic cohort validity requires complete comparable load evidence");
        var lastConfidence = confidencePlot.Points.Last().Score;
        confidenceRange.SelectedIndex = 0;
        Check(confidencePlot.Points.Count == 7 && confidencePlot.Points.Last().Score == lastConfidence, "Changing confidence display range does not retrain the comparison");
        confidenceRange.SelectedIndex = 2;
        Check(confidencePlot.Points.Count == 90 && confidencePlot.Points.Any(p => p.Score == null), "Ninety-day confidence keeps missing history as gaps");
        confidenceRange.SelectedIndex = 1;
        Check(electricalTrend.Points.Any(p => p.Median > 150) && electricalTrendSummary.Text.Contains("425–450 W"), "Electrical chart shows synthetic voltage deterioration at a historical load band");
        electricalTrendMode.SelectedIndex = 1;
        Check(electricalTrendSummary.Text.Contains("Recorded reference") && electricalTrend.Points.Any(p => p.Median > 150), "Electrical chart switches to recorded reference comparison");
        electricalTrendMode.SelectedIndex = 0;
        Check(electricalTrendSummary.Text.Contains("Initial observation"), "Electrical chart labels the observational comparison explicitly");
        Close(); await Task.Delay(50);
        Check(!IsVisible && !exiting && timer.IsEnabled && tray?.Visible == true, "Close hides window while tray and polling stay active");
        Restore(); await Task.Delay(50);
        Check(IsVisible, "Restore reopens the same dashboard");
        bins.SelectedIndex = 1; RenderData(); Check(histogram.Reference == null, "All-load histogram suppresses reference markers");
        bins.SelectedIndex = 0; RenderData(); Check(histogram.Reference?.Median == 12.08, "Load-bin histogram uses saved reference");
        warnings.SelectedIndex = 0; Acknowledge(); Check(acknowledged.Count > 0 && store.Current?.Status == "BASELINE_SHIFT", "Acknowledgement preserves active warning state");
        store.Current!.Time = DateTimeOffset.UtcNow.AddSeconds(-30); UpdateStatus(); Check(voltage.Text == "—" && connection.Text.Contains("No fresh"), "Stale current cards are blank and explicitly unavailable");
        return checks.ToArray();
    }
    public async Task<string[]> RunIntegrationChecks()
    {
        var checks = new List<string>();
        void Check(bool ok, string name) { if (!ok) throw new Exception("Integration check failed: " + name); checks.Add(name); }
        for (int i = 0; i < 10 && store.Current?.Fresh(config.MaxAgeSeconds) != true; i++) { await Task.Delay(300); await Refresh(); }
        Check(connected && store.Current?.Fresh(config.MaxAgeSeconds) == true, "GUI attaches to a live versioned daemon");
        var before = store.Current!.Time;
        Close(); await Task.Delay(TimeSpan.FromSeconds(Math.Max(3, config.SampleSeconds * 3))); await Refresh();
        Check(!IsVisible && tray?.Visible == true && store.Current!.Time > before, "Closing GUI preserves actual daemon sampling and tray icon");
        var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true };
        start.ArgumentList.Add("--config"); start.ArgumentList.Add(configPath);
        using (var duplicate = Process.Start(start)!)
        { await duplicate.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)); Check(duplicate.ExitCode == 0 && IsVisible, "Second GUI launch activates existing window and exits"); }
        Check(await client.Send("hello") != null && await client.Send("stop") != null, "GUI sends identity-bound graceful stop");
        for (int i = 0; i < 20; i++) { await Task.Delay(250); await Refresh(); if (store.Current?.Stopped == true) break; }
        Check(store.Current?.Stopped == true, "Daemon persists stopped state after control request");
        Check(voltage.Text == "—" && store.Incidents.Any(i => i.Status == "MONITOR_STOPPED"), "GUI independently reports monitoring loss and blanks live cards");
        return checks.ToArray();
    }
    public async Task<string> RunUiMemoryChecks()
    {
        if (!demo || !QuietTest) throw new InvalidOperationException("Memory checks require quiet synthetic mode");
        timer.Stop();
        var now = DateTimeOffset.UtcNow;
        for (int i = 0; i < 210000; i++)
        {
            var t = now.AddSeconds((i - 210000) * .2);
            store.Samples.Add(new(t, t, 12.08 + (i % 17) * .0001, 12.1, 440, 425, "NO_SHIFT_DETECTED", 12.1, 12.07, .02, ""));
        }
        var observations = new List<object>(); long firstRetained = 0, lastRetained = 0;
        for (int batch = 0; batch < 4; batch++)
        {
            for (int i = 0; i < 25; i++)
            {
                var t = DateTimeOffset.UtcNow;
                store.Samples.Add(new(t, t, 12.08, 12.1, 440, 425, "NO_SHIFT_DETECTED", 12.1, 12.07, .02, ""));
                RenderData(); UpdateLayout();
                await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);
            }
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            lastRetained = GC.GetTotalMemory(false); if (batch == 0) firstRetained = lastRetained;
            using var process = Process.GetCurrentProcess();
            observations.Add(new { batch, retained_bytes = lastRetained, resident_bytes = process.WorkingSet64, private_bytes = process.PrivateMemorySize64, handles = process.HandleCount, samples = store.Samples.Count });
        }
        if (store.Samples.Count != 200000 || lastRetained > firstRetained + 16 * 1024 * 1024) throw new Exception("GUI memory did not plateau at its history limit");
        Close();
        if (history.Samples.Count != 0 || visiblePoints.Count != 0 || distributionPoints.Count != 0) throw new Exception("Hidden GUI retained an old chart snapshot");
        return JsonSerializer.Serialize(new { passed = true, renders = 100, bounded_samples = store.Samples.Count, hidden_snapshots_released = true, observations }, new JsonSerializerOptions { WriteIndented = true });
    }
    public static string Friendly(string status) => status switch
    {
        "DRIVER_AWAITING_APPROVAL" => "Awaiting maintainer approval", "DRIVER_REVOKED" => "Driver approval withdrawn",
        "DRIVER_APPROVAL_UNAVAILABLE" => "Driver approval unavailable", "APP_UPDATE_REQUIRED" => "App update required",
        "DRIVER_CHANGED" => "Checking the new driver",
        "SUDDEN_DROOP" => "Sudden voltage drop", "BASELINE_SHIFT" => "Sustained voltage shift", "VOLTAGE_UNAVAILABLE" => "Voltage unavailable", "MONITOR_STOPPED" => "Monitoring stopped",
        "LEARNING_REFERENCE" => "Learning reference", "WINDOW_WARMUP" => "Warming up comparison", "LOAD_SETTLING" => "Load settling", "NO_SHIFT_DETECTED" => "No shift detected",
        "OUTSIDE_ANALYSIS_RANGE" => "Waiting for steady load", "POWER_UNAVAILABLE" => "Power unavailable", "WAITING_FOR_FRESH_VOLTAGE" => "Waiting for fresh voltage",
        "SOURCE_UNAVAILABLE" => "Source unavailable", "TIMESTAMP_INVALID" => "Timestamp invalid", "STALE" => "Stale telemetry", "SENSOR_UNCHARACTERIZED" => "Sensor uncharacterized",
        "ANALYSIS_LOAD_UNAVAILABLE" => "Analysis load unavailable", "RESIDUAL_UNAVAILABLE" => "Residual unavailable", "RESIDUAL_GAP" => "Residual gap", "RESIDUAL_IDENTITY_MISMATCH" => "Residual identity mismatch", "RESIDUAL_INVALID" => "Residual invalid",
        "POWER_LIMIT_INCREASE" => "Power-limit increase observed", "POWER_LIMIT_OK" => "Power-limit observation", "POWER_LIMIT_UNAVAILABLE" => "Power-limit data unavailable", _ => status.Replace('_', ' ').ToLowerInvariant()
    };
    sealed record BinChoice(int Value, int Width) { public override string ToString() => $"{Value}–{Value + Width} W"; }
    sealed record ConfidenceCohortChoice(string Key, string Label) { public override string ToString() => Label; }
}
