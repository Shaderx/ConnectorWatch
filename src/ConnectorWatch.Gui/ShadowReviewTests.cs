using System;
using System.Linq;

namespace ConnectorWatch.Gui;

public static class ShadowReviewTests
{
    public static void Run(Action<bool, string> report)
    {
        if (report is null) throw new ArgumentNullException(nameof(report));
        DateTimeOffset now = new(2026, 9, 28, 12, 0, 0, TimeSpan.Zero);
        var history = Enumerable.Range(0, 14)
            .Select(index => Summary(now.AddDays(-index), "run-" + index))
            .ToArray();
        var selected = MainWindow.SelectShadowReviewHistory(history);
        report(selected.Length == 12 && selected[0].RunId == "run-0" && selected[^1].RunId == "run-11",
            "Shadow review history selects the newest 12 completed summaries");

        var failed = new ShadowReviewSnapshot
        {
            DataDirectory = "synthetic-data",
            Status = "FAILED",
            Detail = "Fixture process failed.",
            UpdatedUtc = now,
            LastSuccessUtc = history[0].CompletedUtc,
            NextAttemptUtc = now.AddHours(-1),
            History = history
        };
        var failedDisplay = MainWindow.BuildShadowReviewDisplay(failed, "", monitorAvailable: false, now);
        report(failedDisplay.StatusText.Contains("Latest scheduled review failed", StringComparison.Ordinal) &&
            failedDisplay.StatusText.Contains("Fixture process failed", StringComparison.Ordinal) &&
            failedDisplay.IsStale && failedDisplay.Latest?.RunId == "run-0",
            "A failed review shows its detail, overdue state, and previous completed results");

        var refreshError = MainWindow.BuildShadowReviewDisplay(failed, "Synthetic read error", monitorAvailable: true, now);
        report(refreshError.HasReadError && refreshError.Latest?.RunId == "run-0" &&
            refreshError.StatusText.Contains("Showing the last loaded result", StringComparison.Ordinal),
            "A status read error keeps the last loaded review available");

        var runningOld = failed with
        {
            Status = "RUNNING",
            Detail = "",
            StartedUtc = now.AddMinutes(-18),
            UpdatedUtc = now.AddMinutes(-18),
            NextAttemptUtc = now.AddHours(1)
        };
        var runningFresh = runningOld with { StartedUtc = now.AddMinutes(-16), UpdatedUtc = now.AddMinutes(-16) };
        report(MainWindow.IsShadowReviewStale(runningOld, monitorAvailable: true, now) &&
            !MainWindow.IsShadowReviewStale(runningFresh, monitorAvailable: true, now),
            "Running review state becomes stale after 15 minutes plus the allowance");

        var waiting = MainWindow.BuildShadowReviewDisplay(null, "No state file", monitorAvailable: false, now);
        report(waiting.Latest is null && waiting.HasReadError &&
            waiting.StatusText.Contains("Review status unavailable", StringComparison.Ordinal),
            "Missing status displays a bounded read error without inventing a result");

        string details = MainWindow.FormatShadowReviewDetails(Summary(now, "detail-run"));
        report(details.Contains("CURRENT_LOAD_ONLY", StringComparison.Ordinal) &&
            details.Contains("fair heldout model comparison", StringComparison.OrdinalIgnoreCase) &&
            details.Contains("GPU temperature", StringComparison.Ordinal) &&
            details.Contains("100 rejected", StringComparison.Ordinal) &&
            details.Contains("GRADUAL_RAMP: UNAVAILABLE", StringComparison.Ordinal),
            "Selected review details show eligible metrics, exclusions, GPU temperature, and synthetic states");

        report(MainWindow.IsSafeShadowReviewReportBasename("review-20260928.json") &&
            MainWindow.IsSafeShadowReviewReportBasename("review-20260928.md") &&
            !MainWindow.IsSafeShadowReviewReportBasename("review.exe") &&
            !MainWindow.IsSafeShadowReviewReportBasename("..\\review.json") &&
            !MainWindow.IsSafeShadowReviewReportBasename("review.json:launch") &&
            MainWindow.ResolveShadowReviewReportPath("synthetic-data", Summary(now, "report") with { ReportFileName = "review.exe" }) is null,
            "Report links accept only JSON or Markdown basenames and reject paths and executable files");
    }

    static ShadowReviewSummary Summary(DateTimeOffset completed, string runId) => new()
    {
        RunId = runId,
        CompletedUtc = completed,
        InputCutoffUtc = completed.AddHours(-12),
        State = "SUFFICIENT",
        Conclusion = "Synthetic evidence available",
        WinnerModel = "CURRENT_LOAD_ONLY",
        CohortKey = "GPU-A · test fixture",
        ModelIdentity = "shadow-regression-v2",
        ConfigurationIdentity = "fixture-config",
        FrozenCutoffFingerprint = "fixture-cutoff",
        SupportedDays = 15,
        TestDays = 5,
        FairHeldoutRows = 48,
        TestRows = 62,
        TrainingDays = ["2026-09-10", "2026-09-11"],
        CalibrationDays = ["2026-09-12"],
        AdvisoryTransitions = 2,
        AdvisoryTransitionsPerObservedHour = .5,
        PowerMinimumW = 300,
        PowerMaximumW = 450,
        CurrentMinimumA = 20,
        CurrentMaximumA = 38,
        GpuTemperatureMinimumC = 50,
        GpuTemperatureMaximumC = 65,
        TemperatureValidMinutes = 25,
        SourceRows = 1000,
        RejectedRows = 100,
        InvalidRows = 10,
        Models =
        [
            new ShadowReviewModelSummary
            {
                Name = "CURRENT_LOAD_ONLY",
                IsAvailable = true,
                EligibleForRanking = true,
                FairRows = 48,
                OutOfEnvelopeRows = 2,
                FairMaeV = .06,
                FairRmseV = .08,
                Detail = "Synthetic test model."
            },
            new ShadowReviewModelSummary
            {
                Name = "POWER_LOAD_ONLY",
                IsAvailable = true,
                EligibleForRanking = false,
                FairRows = 48,
                OutOfEnvelopeRows = 3,
                FairMaeV = .09,
                FairRmseV = .12,
                Detail = "Diagnostic only."
            }
        ],
        SyntheticFaults =
        [
            new ShadowReviewSyntheticSummary
            {
                Label = "STEP_200MV",
                ScenarioIdentity = "fixture-step",
                State = "AVAILABLE",
                OnsetUtc = completed.AddHours(-8),
                WindowEndUtc = completed.AddHours(-2),
                DetectionLatencySeconds = 84,
                AttributableTransitions = 1,
                Detail = "Synthetic scenario."
            },
            new ShadowReviewSyntheticSummary
            {
                Label = "GRADUAL_RAMP",
                ScenarioIdentity = "fixture-ramp",
                State = "UNAVAILABLE",
                Detail = "Synthetic evidence was incomplete."
            }
        ],
        Warnings = ["Synthetic fixture warning."],
        ReportFileName = "review-20260928.json"
    };
}
