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

public sealed class MainWindow : Window
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
    readonly TextBlock bannerText = Text("", 13), analysisTitle = Text("Waiting for data", 19), analysisBody = Text("", 12), histogramStats = Text("", 11), footer = Text("", 11);
    readonly Border banner = new() { CornerRadius = new CornerRadius(8), Padding = new Thickness(14, 10, 14, 10), Margin = new Thickness(0, 0, 0, 14) };
    readonly ProgressBar progress = new() { Height = 5, Margin = new Thickness(0, 12, 0, 12), Maximum = 300, Foreground = Palette.Cyan, Background = Palette.Line, BorderThickness = new Thickness(0) };
    readonly HistoryPlot history = new() { Height = 170 };
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
    DateTimeOffset? focusTime;
    DateTimeOffset lastNotification;
    string operation = "";
    public MainWindow(string configPath, GuiConfig config, string data, string settingsPath, bool demo, bool noStart, bool render)
    {
        this.configPath = configPath; this.config = config; this.data = data; this.settingsPath = settingsPath; this.demo = demo; this.noStart = noStart; this.render = render;
        store = new(data); client = new(data);
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
        range.Items.Add("15 minutes"); range.Items.Add("1 hour"); range.Items.Add("24 hours");
        range.SelectedIndex = settings.RangeMinutes == 1440 ? 2 : settings.RangeMinutes == 60 ? 1 : 0;
        range.SelectionChanged += (_, _) => { settings.RangeMinutes = range.SelectedIndex == 2 ? 1440 : range.SelectedIndex == 1 ? 60 : 15; if (!busy) RenderData(); };
        overlay.IsChecked = settings.ShowPcie; overlay.Checked += (_, _) => { settings.ShowPcie = true; if (!busy) RenderData(); }; overlay.Unchecked += (_, _) => { settings.ShowPcie = false; if (!busy) RenderData(); };
        var chartOptions = new StackPanel { Orientation = Orientation.Horizontal }; chartOptions.Children.Add(overlay); chartOptions.Children.Add(range);
        range.Margin = new Thickness(14, 0, 0, 0);
        var legend = Text("16-pin input  •  cyan     PCIe supply  •  violet     Alert  •  red", 10); legend.Foreground = Palette.Muted;
        var chartPanel = Panel(Vertical(Header("Voltage history", chartOptions), history, legend)); chartPanel.Margin = new Thickness(0, 0, 0, 16); content.Children.Add(chartPanel);
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
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 16, 0, 12) };
        var details = Button("Sensor details"); details.Click += (_, _) => ShowDetails();
        var settingsButton = Button("Settings"); settingsButton.Click += (_, _) => ShowSettings();
        var logs = Button("Open logs"); logs.Click += (_, _) => OpenPath(data);
        var export = Button("Export visible CSV"); export.Click += (_, _) => Export();
        resumeLive.Visibility = Visibility.Collapsed; resumeLive.Click += (_, _) => { focusTime = null; RenderData(); };
        foreach (var b in new[] { details, settingsButton, logs, export, resumeLive }) actions.Children.Add(b);
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
    }
    async Task Refresh()
    {
        if (busy || exiting) return; busy = true;
        try
        {
            if (!demo)
            {
                connected = await client.Send("hello") != null;
                if (connected) await client.Send("lease");
                await store.RefreshAsync();
            }
            var s = store.Current; bool fresh = s?.Fresh(config.MaxAgeSeconds) == true;
            if (firstPoll) { foreach (var w in store.Incidents) notified.Add(w.Id); firstPoll = false; }
            if (fresh) { hadFresh = true; lossNotified = false; }
            if (!fresh && (hadFresh || s != null) && !lossNotified)
            {
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
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { operation = ex.Message; UpdateStatus(); }
        finally { busy = false; }
    }
    void UpdateStatus()
    {
        var s = store.Current; bool fresh = s?.Fresh(config.MaxAgeSeconds) == true;
        double age = s == null ? 0 : Math.Max(0, (DateTimeOffset.UtcNow - s.Time).TotalSeconds);
        string state = fresh ? (demo ? "Synthetic preview" : "Monitoring") : s?.Stopped == true ? "Monitor stopped" : "No fresh telemetry";
        connection.Text = state + (s == null ? "" : $"  ·  {age:F0}s since update");
        connection.Foreground = fresh ? Palette.Cyan : Palette.Amber;
        voltage.Text = fresh && s?.Voltage is double v ? $"{v:F3} V" : "—";
        pcie.Text = fresh && s?.Pcie is double pv ? $"{pv:F3} V" : "—";
        power.Text = fresh && s?.Power is double pw ? $"{pw:F1} W" : "—";
        bool direct = demo || s?.Source.StartsWith("direct NVIDIA", StringComparison.OrdinalIgnoreCase) == true;
        powerLabel.Text = direct ? "CONNECTOR POWER" : "COMPARISON POWER";
        powerNote.Text = direct ? "Measured rail V × A" : "Power supplied by voltage source";
        change.Text = fresh && s?.Drop is double dr ? $"{-dr * 1000:+0;−0;0} mV" : "—";
        referenceNote.Text = s?.Reference is double r ? $"Reference {r:F3} V" : "Awaiting a learned reference";
        bool alert = fresh && s?.Status is "SUDDEN_DROOP" or "BASELINE_SHIFT";
        string message = operation.Length > 0 ? operation : store.ReadError.Length > 0 ? "Data read issue: " + store.ReadError : !fresh ? "Monitoring unavailable. Values shown in history are past observations." : alert ? Friendly(s!.Status) + ". Save your work and reduce GPU load while investigating." : s!.Status == "OUTSIDE_ANALYSIS_RANGE" ? $"Collecting telemetry. Analysis starts during steady connector load above {config.MinAnalysisWatts} W." : Friendly(s!.Status) + ". Aggregate rail readings do not certify connector safety.";
        if (demo) message = "Synthetic demonstration — charts and warnings below are test data.";
        bannerText.Text = message; bannerText.Foreground = alert ? Palette.Red : Palette.Muted;
        banner.Background = Palette.Panel; banner.BorderBrush = alert ? Palette.Red : Palette.Line; banner.BorderThickness = new Thickness(1);
        if (tray != null)
        {
            tray.Icon = !fresh ? Drawing.SystemIcons.Error : alert ? Drawing.SystemIcons.Warning : Drawing.SystemIcons.Information;
            string text = $"ConnectorWatch · {state}\n{s?.Voltage:F3} V · {age:F0}s ago"; tray.Text = text.Length > 63 ? text[..63] : text;
        }
    }
    int? SelectedBin()
    {
        if (bins.SelectedIndex == 1) return null;
        if (bins.SelectedItem is BinChoice b) return b.Value;
        return store.Current?.Bin;
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
        analysisTitle.Text = s == null || !s.Fresh(config.MaxAgeSeconds) ? "Monitoring unavailable" : Friendly(s.Status);
        progress.Maximum = config.BaselineSamples; progress.Value = s?.Reference.HasValue == true ? config.BaselineSamples : Math.Min(config.BaselineSamples, s?.Learning ?? 0);
        string binLabel = s?.Bin is int bnow ? $"{bnow}–{bnow + config.BinWatts} W connector load" : "No eligible load bin";
        analysisBody.Text = binLabel + "\n" + (s?.Reference.HasValue == true ? $"Frozen reference  {s.Reference:F3} V\nCurrent median  {s.Median:F3} V  ·  P05 {s.P05:F3} V\nWindow  {s.Window} / {config.WindowSamples} eligible samples" : $"Reference learning  {s?.Learning ?? 0} / {config.BaselineSamples} samples\nLearning accumulates during steady load.") + $"\nShift threshold  {config.ShiftVolts * 1000:F0} mV  ·  sudden {config.SuddenDroopVolts * 1000:F0} mV";
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
        ShowText("Sensor details", $"GPU UUID\n{(string.IsNullOrWhiteSpace(s?.GpuUuid) ? (string.IsNullOrWhiteSpace(config.GpuUuid) ? "Automatic (awaiting daemon)" : config.GpuUuid) : s.GpuUuid)}\n\nSource\n{s?.Source}\n\nLast observation\n{s?.Time.LocalDateTime:yyyy-MM-dd HH:mm:ss}\n16-pin {s?.Voltage:F6} V  ·  PCIe {s?.Pcie:F6} V\n16-pin current {s?.Current:F3} A  ·  PCIe current {s?.PcieCurrent:F3} A\nConnector power {s?.Power:F3} W  ·  NVML board power {s?.BoardPower:F3} W\nGPU {s?.Temperature:F0} °C  ·  utilization {s?.Utilization:F0}%  ·  power limit {s?.Limit:F0} W\n\nStatus\n{s?.Status}  ·  schema {s?.Schema}\n{s?.Detail}\n\nTimestamps are host polling time. Native sensor freshness is unverified. GPU temperature is not connector temperature.\n\nData directory\n{data}");
    }
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
    public void SessionEnding() { exiting = true; timer.Stop(); SaveSettings(); tray?.Dispose(); }
    public async Task ExitGui()
    {
        if (exiting) return; exiting = true; timer.Stop();
        if (!demo) await client.Send("release");
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
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { operation = "Could not save dashboard preferences: " + ex.Message; }
    }
    void ThemeChanged(object? sender, PropertyChangedEventArgs e) { if (e.PropertyName == nameof(SystemParameters.HighContrast)) { Background = Palette.Bg; Foreground = Palette.Text; history.InvalidateVisual(); histogram.InvalidateVisual(); } }
    void FillDemo()
    {
        var now = DateTimeOffset.UtcNow; var random = new Random(5090);
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
        store.Current = new Snapshot { Time = now, Voltage = last.Voltage, Pcie = last.Pcie, Power = last.Power, BoardPower = 452, Temperature = 62, Utilization = 97, Limit = 450, Bin = 425, Status = "BASELINE_SHIFT", Reference = 12.08, Median = 11.856, P05 = 11.849, Drop = .224, Learning = 300, Window = 60, Schema = 2, Source = "Synthetic fixture · no hardware" };
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
        "SUDDEN_DROOP" => "Sudden voltage drop", "BASELINE_SHIFT" => "Sustained voltage shift", "VOLTAGE_UNAVAILABLE" => "Voltage unavailable", "MONITOR_STOPPED" => "Monitoring stopped",
        "LEARNING_REFERENCE" => "Learning reference", "WINDOW_WARMUP" => "Warming up comparison", "LOAD_SETTLING" => "Load settling", "NO_SHIFT_DETECTED" => "No shift detected",
        "OUTSIDE_ANALYSIS_RANGE" => "Waiting for steady load", "POWER_UNAVAILABLE" => "Power unavailable", "WAITING_FOR_FRESH_VOLTAGE" => "Waiting for fresh voltage", _ => status.Replace('_', ' ').ToLowerInvariant()
    };
    sealed record BinChoice(int Value, int Width) { public override string ToString() => $"{Value}–{Value + Width} W"; }
}
