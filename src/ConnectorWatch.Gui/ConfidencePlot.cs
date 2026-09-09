using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Media;

namespace ConnectorWatch.Gui;

public sealed class ConfidencePlot : Plot
{
    public IReadOnlyList<ConfidencePoint> Points { get; set; } = Array.Empty<ConfidencePoint>();
    public DateTimeOffset From { get; set; }
    public DateTimeOffset To { get; set; }
    public string EmptyText { get; set; } = "Learning — needs three comparable days.";
    public ConfidencePlot()
    {
        Focusable = true;
        System.Windows.Automation.AutomationProperties.SetName(this,
            "Electrical degradation confidence over days. Higher values mean stronger evidence of persistent voltage decline. Missing data is not zero confidence.");
        MouseMove += (_, e) =>
        {
            if (Points.Count == 0) { ToolTip = EmptyText; return; }
            var f = Math.Clamp((e.GetPosition(this).X - Area.Left) / Area.Width, 0, 1);
            var time = From.AddSeconds((To - From).TotalSeconds * f);
            var point = Points.MinBy(p => Math.Abs((p.Day.AddHours(12) - time).TotalSeconds))!;
            ToolTip = $"{point.Day:dd MMM yyyy} (UTC day)\n" + (point.Score.HasValue
                ? $"Degradation confidence: {point.Score:F0}%\n{point.EvidenceDays} comparable days in the previous week\nTypical recorded-reference drop: {point.MedianDropMv:F1} mV\n" + (point.Unverified ? "Sensor timing or reference remains unverified." : "Based on recorded measurement metadata.")
                : "No score: " + Reason(point.Reason));
        };
    }
    public static string Reason(string reason)
    {
        reason ??= "";
        return reason switch
        {
            "MISSING" => "the day has no recorded history",
            "CONTEXT_UNKNOWN" => "telemetry context is unavailable",
            "MEDIAN_UNAVAILABLE" => "the daily voltage-drop summary is unavailable",
            "LEARNING" or "LEARNING_REFERENCE" => "reference learning is still in progress",
            "LOAD_NOT_COMPARABLE" => "workload differs from the comparison period",
            "EPOCH_CHANGED" => "comparison changed; learning again",
            "INSUFFICIENT_DAYS" or "INSUFFICIENT_EVIDENCE" => "needs three comparable days",
            "INSUFFICIENT_EXPOSURE" or "INSUFFICIENT_DAY_EVIDENCE" => "not enough comparable loaded measurements that day",
            "AMBIGUOUS_EPOCH" => "the reference changed during that day",
            _ when reason.Contains("LEARNING", StringComparison.OrdinalIgnoreCase) => "reference learning is still in progress",
            _ when reason.Contains("UNAVAILABLE", StringComparison.OrdinalIgnoreCase) || reason.Contains("TRUNCATED", StringComparison.OrdinalIgnoreCase) => "recorded measurements are unavailable or incomplete",
            _ => "insufficient comparable measurements"
        };
    }
    protected override void OnRender(DrawingContext d)
    {
        base.OnRender(d);
        var a = Area;
        if (To <= From || From == default) { Label(d, EmptyText, a.Left, a.Top + 40); return; }
        double X(DateTimeOffset time) => a.Left + Math.Clamp((time - From).TotalDays / Math.Max(1, (To - From).TotalDays), 0, 1) * a.Width;
        double Y(double score) => a.Bottom - Math.Clamp(score, 0, 100) / 100 * a.Height;
        for (int i = 0; i <= 4; i++)
        {
            double score = i * 25;
            d.DrawLine(new Pen(Palette.Line, 1), new(a.Left, Y(score)), new(a.Right, Y(score)));
            Label(d, score.ToString("F0") + "%", 5, Y(score) - 7);
        }
        Label(d, From.ToString("dd MMM"), a.Left, a.Bottom + 9);
        Label(d, From.AddDays((To - From).TotalDays / 2).ToString("dd MMM"), a.Left + a.Width / 2 - 20, a.Bottom + 9);
        Label(d, To.AddDays(-1).ToString("dd MMM"), a.Right - 40, a.Bottom + 9);
        if (!Points.Any(p => p.Score.HasValue))
        { Label(d, EmptyText, a.Left + 15, a.Top + a.Height / 2 - 8, Palette.Text, 13); return; }
        ConfidencePoint? previous = null;
        foreach (var point in Points)
        {
            if (!point.Score.HasValue) { previous = null; continue; }
            var at = new Point(X(point.Day.AddHours(12)), Y(point.Score.Value));
            Brush brush = point.Score >= 70 ? Palette.Red : point.Score >= 35 ? Palette.Amber : Palette.Cyan;
            if (previous?.Score.HasValue == true && previous.Epoch == point.Epoch && (point.Day - previous.Day).TotalDays == 1)
                d.DrawLine(new Pen(brush, 2.2), new(X(previous.Day.AddHours(12)), Y(previous.Score.Value)), at);
            d.DrawEllipse(brush, null, at, 2.5, 2.5);
            previous = point;
        }
    }
}
