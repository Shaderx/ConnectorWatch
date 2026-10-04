using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace ConnectorWatch.Gui;

public sealed partial class MainWindow
{
    const int ShadowReviewHistoryLimit = 12;
    static readonly TimeSpan ShadowReviewPollInterval = TimeSpan.FromSeconds(30);
    static readonly TimeSpan ShadowReviewStaleAllowance = TimeSpan.FromMinutes(2);

    readonly DispatcherTimer shadowReviewTimer = new() { Interval = TimeSpan.FromSeconds(30) };
    readonly ComboBox shadowReviewHistory = new() { MinWidth = 240 };
    readonly TextBlock shadowReviewStatus = Text("Waiting for review status", 12);
    readonly TextBlock shadowReviewSchedule = Text("", 10);
    readonly TextBlock shadowReviewResult = Text("No completed review is available yet.", 12);
    readonly TextBlock shadowReviewDetails = Text("", 10);
    readonly Button shadowReviewReport = Button("Open selected review report");
    readonly Expander shadowReviewExpander = new() { Header = "Coverage, models and scenarios", Foreground = Palette.Muted, Margin = new Thickness(0, 6, 0, 0) };

    ShadowReviewSnapshot? shadowReviewSnapshot;
    string shadowReviewReadError = "";
    string selectedShadowReviewRunId = "";
    DateTimeOffset lastShadowReviewReadUtc;
    bool shadowReviewReadBusy;
    bool updatingShadowReviewHistory;

    internal FrameworkElement? ShadowReviewPreview { get; private set; }

    bool UsesSyntheticShadowReview => demo || render || QuietTest || shadowReviewDemoOnly;

    UIElement BuildShadowReviewPanel()
    {
        var disclosure = Text(
            "Offline replay only. Live alerts and accepted references remain unchanged. " +
            "Synthetic scenarios are not probabilities of hardware damage.", 10);
        disclosure.Foreground = Palette.Muted;

        shadowReviewStatus.Foreground = Palette.Amber;
        shadowReviewSchedule.Foreground = Palette.Muted;
        shadowReviewResult.Margin = new Thickness(0, 4, 0, 4);
        shadowReviewHistory.SelectionChanged += (_, _) =>
        {
            if (updatingShadowReviewHistory) return;
            selectedShadowReviewRunId = (shadowReviewHistory.SelectedItem as ShadowReviewHistoryChoice)?.RunId ?? "";
            RenderShadowReviewDetails();
        };
        shadowReviewReport.Click += (_, _) => OpenSelectedShadowReviewReport();

        var historyRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 8) };
        var historyLabel = Text("Review history", 10);
        historyLabel.Foreground = Palette.Muted;
        historyLabel.VerticalAlignment = VerticalAlignment.Center;
        historyLabel.Margin = new Thickness(0, 0, 10, 0);
        historyRow.Children.Add(historyLabel);
        historyRow.Children.Add(shadowReviewHistory);

        var detailContent = Vertical(historyRow, shadowReviewDetails, shadowReviewReport);
        shadowReviewExpander.Content = detailContent;
        shadowReviewExpander.IsExpanded = UsesSyntheticShadowReview;

        var panel = Panel(Vertical(
            Header("Experimental shadow review"),
            shadowReviewStatus,
            shadowReviewSchedule,
            shadowReviewResult,
            disclosure,
            shadowReviewExpander));
        panel.Margin = new Thickness(0, 0, 0, 16);
        ShadowReviewPreview = panel;

        shadowReviewTimer.Tick += async (_, _) => await PollShadowReviewAsync();
        RenderShadowReviewPanel();
        return panel;
    }

    async Task InitializeShadowReviewAsync()
    {
        if (UsesSyntheticShadowReview)
        {
            SetShadowReviewSnapshot(CreateSyntheticShadowReview(DateTimeOffset.UtcNow));
            return;
        }

        await PollShadowReviewAsync(force: true);
        shadowReviewTimer.Start();
    }

    async Task PollShadowReviewAsync(bool force = false)
    {
        if (UsesSyntheticShadowReview || shadowReviewReadBusy || exiting) return;

        DateTimeOffset now = DateTimeOffset.UtcNow;
        if (!force && now - lastShadowReviewReadUtc < ShadowReviewPollInterval) return;

        shadowReviewReadBusy = true;
        lastShadowReviewReadUtc = now;
        try
        {
            var read = await Task.Run(() =>
            {
                var snapshot = ShadowReviewStore.Read(data, out string? error);
                return (Snapshot: snapshot, Error: error);
            });

            if (read.Snapshot is not null)
            {
                shadowReviewSnapshot = read.Snapshot;
                shadowReviewReadError = "";
            }
            else
            {
                shadowReviewReadError = string.IsNullOrWhiteSpace(read.Error)
                    ? "No review status file is available yet."
                    : read.Error;
            }
            RenderShadowReviewPanel();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or
                                   InvalidDataException or ArgumentException or NotSupportedException)
        {
            shadowReviewReadError = ex.Message;
            GuiLog.Current.Write("shadow_review_read_error", new { data }, ex, throttle: true);
            RenderShadowReviewPanel();
        }
        finally
        {
            shadowReviewReadBusy = false;
        }
    }

    void SetShadowReviewSnapshot(ShadowReviewSnapshot snapshot, string readError = "")
    {
        shadowReviewSnapshot = snapshot;
        shadowReviewReadError = readError;
        RenderShadowReviewPanel();
    }

    void RenderShadowReviewPanel()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        ShadowReviewDisplay display = BuildShadowReviewDisplay(
            shadowReviewSnapshot, shadowReviewReadError, connected, now);

        shadowReviewStatus.Text = display.StatusText;
        shadowReviewStatus.Foreground = display.IsStale || display.IsFailure || display.HasReadError
            ? Palette.Amber
            : Palette.Muted;
        shadowReviewSchedule.Text = display.ScheduleText;
        shadowReviewResult.Text = display.Latest is null
            ? "No completed review is available yet."
            : FormatReviewHeadline(display.Latest);

        updatingShadowReviewHistory = true;
        try
        {
            shadowReviewHistory.Items.Clear();
            foreach (var item in SelectShadowReviewHistory(shadowReviewSnapshot?.History))
            {
                string label = $"{item.CompletedUtc.ToLocalTime():g} · {item.State} · {item.TestDays} test days";
                shadowReviewHistory.Items.Add(new ShadowReviewHistoryChoice(item.RunId, label));
            }

            var selected = shadowReviewHistory.Items.OfType<ShadowReviewHistoryChoice>()
                .FirstOrDefault(item => item.RunId == selectedShadowReviewRunId)
                ?? shadowReviewHistory.Items.OfType<ShadowReviewHistoryChoice>().FirstOrDefault();
            shadowReviewHistory.SelectedItem = selected;
            selectedShadowReviewRunId = selected?.RunId ?? "";
        }
        finally
        {
            updatingShadowReviewHistory = false;
        }

        RenderShadowReviewDetails();
    }

    void RenderShadowReviewDetails()
    {
        var summary = shadowReviewSnapshot?.History
            .FirstOrDefault(item => item.RunId == selectedShadowReviewRunId);
        shadowReviewDetails.Text = summary is null
            ? "No review details are available. The scheduler keeps the last 12 completed review summaries."
            : FormatShadowReviewDetails(summary);

        shadowReviewReport.IsEnabled = ResolveShadowReviewReportPath(data, summary) is not null;
        shadowReviewReport.ToolTip = shadowReviewReport.IsEnabled
            ? "Open the selected immutable report."
            : "The selected report is unavailable or has an invalid file name.";
    }

    void OpenSelectedShadowReviewReport()
    {
        var summary = shadowReviewSnapshot?.History
            .FirstOrDefault(item => item.RunId == selectedShadowReviewRunId);
        string? path = ResolveShadowReviewReportPath(data, summary);
        if (path is null) return;

        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException)
        {
            ShowText("Could not open review report", ex.Message);
        }
    }

    internal static ShadowReviewDisplay BuildShadowReviewDisplay(
        ShadowReviewSnapshot? snapshot, string readError, bool monitorAvailable, DateTimeOffset now)
    {
        ShadowReviewSummary? latest = SelectShadowReviewHistory(snapshot?.History).FirstOrDefault();
        bool stale = snapshot is not null && IsShadowReviewStale(snapshot, monitorAvailable, now);
        bool failed = snapshot?.Status == "FAILED";
        bool hasReadError = !string.IsNullOrWhiteSpace(readError);

        string status;
        if (snapshot is null)
        {
            status = hasReadError ? "Review status unavailable: " + readError : "Waiting for the first review status.";
        }
        else if (hasReadError)
        {
            status = "Status refresh failed. Showing the last loaded result. " + readError;
        }
        else
        {
            status = snapshot.Status switch
            {
                "WAITING" => "Waiting for the first scheduled review.",
                "RUNNING" => "A scheduled offline review is running.",
                "COMPLETED" => "Latest scheduled review completed.",
                "FAILED" => "Latest scheduled review failed: " + NonEmpty(snapshot.Detail, "No failure detail was recorded."),
                "STOPPED" => "Review scheduler is stopped.",
                _ => "Review scheduler status is unavailable."
            };
        }

        if (stale) status += " Review status is stale.";
        string schedule = snapshot is null
            ? "Next review and completion times are unavailable."
            : FormatSchedule(snapshot, latest);
        return new ShadowReviewDisplay(status, schedule, latest, stale, failed, hasReadError);
    }

    internal static bool IsShadowReviewStale(
        ShadowReviewSnapshot snapshot, bool monitorAvailable, DateTimeOffset now)
    {
        if (snapshot.Status == "RUNNING")
        {
            DateTimeOffset started = snapshot.StartedUtc ?? snapshot.UpdatedUtc;
            if (started != default && now - started > TimeSpan.FromMinutes(15) + ShadowReviewStaleAllowance)
                return true;
        }

        return !monitorAvailable && snapshot.NextAttemptUtc is DateTimeOffset next && next < now;
    }

    internal static ShadowReviewSummary[] SelectShadowReviewHistory(ShadowReviewSummary[]? history) =>
        (history ?? Array.Empty<ShadowReviewSummary>())
            .OrderByDescending(item => item.CompletedUtc)
            .Take(ShadowReviewHistoryLimit)
            .ToArray();

    internal static string? ResolveShadowReviewReportPath(string dataDirectory, ShadowReviewSummary? summary)
    {
        string? name = summary?.ReportFileName;
        if (!IsSafeShadowReviewReportBasename(name)) return null;
        string safeName = name!;

        try
        {
            string reports = Path.GetFullPath(Path.Combine(ShadowReviewStore.DirectoryFor(dataDirectory), "reports"));
            string candidate = Path.GetFullPath(Path.Combine(reports, safeName));
            if (!string.Equals(Path.GetDirectoryName(candidate), reports,
                    OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal) ||
                !File.Exists(candidate)) return null;
            return candidate;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    internal static bool IsSafeShadowReviewReportBasename(string? name)
    {
        if (string.IsNullOrWhiteSpace(name) ||
            !string.Equals(Path.GetFileName(name), name, StringComparison.Ordinal) ||
            name.Contains('/') || name.Contains('\\') ||
            !name.EndsWith(".json", StringComparison.OrdinalIgnoreCase) &&
            !name.EndsWith(".md", StringComparison.OrdinalIgnoreCase)) return false;
        return name.All(character => char.IsAsciiLetterOrDigit(character) || character is '.' or '-' or '_');
    }

    static string FormatReviewHeadline(ShadowReviewSummary summary)
    {
        var eligible = summary.Models
            .Where(model => model.IsAvailable && model.EligibleForRanking && model.FairMaeV.HasValue)
            .OrderBy(model => model.FairMaeV)
            .FirstOrDefault();
        string winner = !string.IsNullOrWhiteSpace(summary.WinnerModel)
            ? summary.WinnerModel!
            : eligible?.Name ?? "No eligible model";
        string mae = eligible is null
            ? ""
            : $" · best fair MAE {FormatNumber(eligible.FairMaeV, "F4")} V ({eligible.Name})";
        return $"{summary.Conclusion} · {winner}{mae}";
    }

    static string FormatSchedule(ShadowReviewSnapshot snapshot, ShadowReviewSummary? latest)
    {
        string last = snapshot.LastSuccessUtc is DateTimeOffset completed
            ? completed.ToLocalTime().ToString("g", CultureInfo.CurrentCulture)
            : latest is not null
                ? latest.CompletedUtc.ToLocalTime().ToString("g", CultureInfo.CurrentCulture)
                : "none yet";
        string next = snapshot.NextAttemptUtc is DateTimeOffset attempt
            ? attempt.ToLocalTime().ToString("g", CultureInfo.CurrentCulture)
            : "not scheduled";
        return $"Last successful review (local): {last} · Next attempt (local): {next}";
    }

    internal static string FormatShadowReviewDetails(ShadowReviewSummary summary)
    {
        var b = new StringBuilder();
        b.AppendLine($"Selected run: {summary.RunId} · {summary.State} · {summary.Conclusion}");
        b.AppendLine($"Completed (local): {summary.CompletedUtc.ToLocalTime():g} · input cutoff (exclusive UTC): {summary.InputCutoffUtc.ToUniversalTime().ToString("yyyy-MM-dd HH:mm:ss 'UTC'", CultureInfo.InvariantCulture)}");
        b.AppendLine($"Cohort: {NonEmpty(summary.CohortKey, "unavailable")} · winner: {NonEmpty(summary.WinnerModel, "none")}");
        b.AppendLine($"Coverage: {summary.SupportedDays} supported days · {summary.TestDays} test days · {summary.TestRows:N0} test rows · {summary.FairHeldoutRows:N0} fair heldout rows");
        b.AppendLine($"Training UTC days: {FormatKeys(summary.TrainingDays)} · calibration UTC days: {FormatKeys(summary.CalibrationDays)}");
        b.AppendLine($"Exclusions: {summary.SourceRows:N0} source rows · {summary.RejectedRows:N0} rejected · {summary.InvalidRows:N0} invalid");
        b.AppendLine($"Load: {FormatRange(summary.PowerMinimumW, summary.PowerMaximumW, "W")} · current: {FormatRange(summary.CurrentMinimumA, summary.CurrentMaximumA, "A")}");
        b.AppendLine($"GPU temperature: {FormatRange(summary.GpuTemperatureMinimumC, summary.GpuTemperatureMaximumC, "°C")} · {summary.TemperatureValidMinutes:N0} valid minutes; this is not connector temperature.");
        b.AppendLine($"Advisory transitions: {summary.AdvisoryTransitions:N0} · {FormatNumber(summary.AdvisoryTransitionsPerObservedHour, "F2")} per observed hour");
        b.AppendLine();
        b.AppendLine("Fair heldout model comparison (lower MAE is better):");
        if (summary.Models.Length == 0) b.AppendLine("  No model results.");
        foreach (var model in summary.Models)
        {
            string eligibility = model.EligibleForRanking ? "eligible" : "not eligible";
            string availability = model.IsAvailable ? eligibility : "unavailable";
            b.AppendLine($"  {model.Name}: MAE {FormatNumber(model.FairMaeV, "F4")} V · RMSE {FormatNumber(model.FairRmseV, "F4")} V · {model.FairRows:N0} fair rows · {model.OutOfEnvelopeRows:N0} outside envelope · {availability}");
            if (!string.IsNullOrWhiteSpace(model.Detail)) b.AppendLine("    " + model.Detail);
        }
        b.AppendLine();
        b.AppendLine("Synthetic scenario outcomes:");
        if (summary.SyntheticFaults.Length == 0) b.AppendLine("  No synthetic scenarios were recorded.");
        foreach (var scenario in summary.SyntheticFaults)
        {
            string latency = scenario.DetectionLatencySeconds is double seconds
                ? $"{seconds:F1} s detection latency"
                : scenario.State == "AVAILABLE" && scenario.AttributableTransitions == 0
                    ? "No attributable advisory in window"
                    : "latency unavailable";
            string window = scenario.OnsetUtc is DateTimeOffset onset && scenario.WindowEndUtc is DateTimeOffset end
                ? $" · onset {onset.ToLocalTime():g} · window end {end.ToLocalTime():g}"
                : "";
            b.AppendLine($"  {scenario.Label}: {scenario.State} · {latency} · {scenario.AttributableTransitions:N0} attributable transitions{window}");
            if (!string.IsNullOrWhiteSpace(scenario.Detail)) b.AppendLine("    " + scenario.Detail);
        }
        if (summary.Warnings.Length > 0)
        {
            b.AppendLine();
            b.AppendLine("Warnings:");
            foreach (string warning in summary.Warnings) b.AppendLine("  " + warning);
        }
        b.AppendLine();
        b.AppendLine($"Model identity: {NonEmpty(summary.ModelIdentity, "unavailable")}");
        b.AppendLine($"Configuration identity: {NonEmpty(summary.ConfigurationIdentity, "unavailable")}");
        b.AppendLine($"Frozen cutoff fingerprint: {NonEmpty(summary.FrozenCutoffFingerprint, "unavailable")}");
        b.Append($"Scenario identities: {FormatKeys(summary.SyntheticFaults.Select(item => item.ScenarioIdentity).ToArray())}");
        return b.ToString();
    }

    static string FormatRange(double? minimum, double? maximum, string unit) =>
        minimum.HasValue && maximum.HasValue
            ? $"{minimum.Value.ToString("F1", CultureInfo.InvariantCulture)}–{maximum.Value.ToString("F1", CultureInfo.InvariantCulture)} {unit}"
            : "unavailable";

    static string FormatNumber(double? value, string format) =>
        value.HasValue ? value.Value.ToString(format, CultureInfo.InvariantCulture) : "unavailable";

    static string FormatKeys(string[] values) =>
        values.Length == 0 ? "none" : string.Join(", ", values);

    static string NonEmpty(string? value, string fallback) =>
        string.IsNullOrWhiteSpace(value) ? fallback : value;

    static ShadowReviewSnapshot CreateSyntheticShadowReview(DateTimeOffset now)
    {
        var history = Enumerable.Range(0, 3)
            .Select(index => CreateSyntheticSummary(now.AddDays(-index)))
            .ToArray();
        return new ShadowReviewSnapshot
        {
            DataDirectory = Path.Combine(Path.GetTempPath(), "ConnectorWatch-shadow-review-demo"),
            Status = "COMPLETED",
            Detail = "Synthetic preview. No monitor, telemetry store, or review process was accessed.",
            UpdatedUtc = now,
            StartedUtc = now.AddMinutes(-2),
            LastSuccessUtc = history[0].CompletedUtc,
            NextAttemptUtc = now.AddDays(7),
            History = history
        };
    }

    static ShadowReviewSummary CreateSyntheticSummary(DateTimeOffset completed)
    {
        DateTimeOffset cutoff = new(completed.UtcDateTime.Date, TimeSpan.Zero);
        DateTimeOffset windowStart = cutoff.AddDays(-10).AddHours(12);
        DateTimeOffset onset = windowStart.AddMinutes(30);
        DateTimeOffset windowEnd = windowStart.AddHours(6);
        return new ShadowReviewSummary
        {
            RunId = "demo-" + completed.ToString("yyyyMMdd", CultureInfo.InvariantCulture),
            CompletedUtc = completed,
            InputCutoffUtc = cutoff,
            State = "SUFFICIENT",
            Conclusion = "Sufficient evidence for an offline comparison",
            WinnerModel = "CURRENT_LOAD_ONLY",
            CohortKey = "synthetic-gpu · 400–450 W",
            ModelIdentity = "shadow-regression-v2 · synthetic",
            ConfigurationIdentity = "demo-config-7d30m6h",
            FrozenCutoffFingerprint = "demo-cutoff-fingerprint",
            SupportedDays = 15,
            TestDays = 10,
            FairHeldoutRows = 480,
            TestRows = 620,
            TrainingDays = Enumerable.Range(0, 3).Select(offset => cutoff.AddDays(-15 + offset).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)).ToArray(),
            CalibrationDays = Enumerable.Range(0, 2).Select(offset => cutoff.AddDays(-12 + offset).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)).ToArray(),
            AdvisoryTransitions = 5,
            AdvisoryTransitionsPerObservedHour = .8,
            PowerMinimumW = 392,
            PowerMaximumW = 498,
            CurrentMinimumA = 31.2,
            CurrentMaximumA = 40.8,
            GpuTemperatureMinimumC = 51,
            GpuTemperatureMaximumC = 68,
            TemperatureValidMinutes = 250,
            SourceRows = 16_420,
            RejectedRows = 27,
            InvalidRows = 11,
            Models =
            [
                new ShadowReviewModelSummary { Name = "POWER_BOARD_TEMPERATURE", IsAvailable = true, EligibleForRanking = false, FairRows = 480, FairMaeV = .106, FairRmseV = .139, OutOfEnvelopeRows = 4, Detail = "Diagnostic only: power is target-coupled in this synthetic fixture." },
                new ShadowReviewModelSummary { Name = "POWER_LOAD_ONLY", IsAvailable = true, EligibleForRanking = false, FairRows = 480, FairMaeV = .114, FairRmseV = .151, OutOfEnvelopeRows = 6, Detail = "Diagnostic only: power is target-coupled in this synthetic fixture." },
                new ShadowReviewModelSummary { Name = "CURRENT_BOARD_TEMPERATURE", IsAvailable = true, EligibleForRanking = true, FairRows = 480, FairMaeV = .071, FairRmseV = .094, OutOfEnvelopeRows = 5 },
                new ShadowReviewModelSummary { Name = "CURRENT_LOAD_ONLY", IsAvailable = true, EligibleForRanking = true, FairRows = 480, FairMaeV = .064, FairRmseV = .087, OutOfEnvelopeRows = 5 }
            ],
            SyntheticFaults =
            [
                new ShadowReviewSyntheticSummary { Label = "STEP_200MV", ScenarioIdentity = "demo-step-scenario-id", State = "AVAILABLE", OnsetUtc = onset, WindowEndUtc = windowEnd, DetectionLatencySeconds = 84, AttributableTransitions = 2, Detail = "Synthetic replay window. No physical intervention." },
                new ShadowReviewSyntheticSummary { Label = "GRADUAL_RAMP", ScenarioIdentity = "demo-ramp-scenario-id", State = "UNAVAILABLE", OnsetUtc = onset, WindowEndUtc = windowEnd, Detail = "Synthetic replay evidence was incomplete." }
            ],
            Warnings = ["Synthetic preview values are illustrative."],
            ReportFileName = ""
        };
    }
}

internal sealed record ShadowReviewDisplay(
    string StatusText,
    string ScheduleText,
    ShadowReviewSummary? Latest,
    bool IsStale,
    bool IsFailure,
    bool HasReadError);

internal sealed record ShadowReviewHistoryChoice(string RunId, string Label)
{
    public override string ToString() => Label;
}
