using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace ConnectorWatch.Gui;

public static class Palette
{
    static readonly Brush bg = Brush("#10151E"), panel = Brush("#18202C"), text = Brush("#EDF2F8"), muted = Brush("#A2B0C3"), cyan = Brush("#57D7E7"), violet = Brush("#B29BF6"), amber = Brush("#F2C16B"), red = Brush("#FF818E"), line = Brush("#2A3545");
    public static Brush Bg => SystemParameters.HighContrast ? SystemColors.WindowBrush : bg;
    public static Brush Panel => SystemParameters.HighContrast ? SystemColors.WindowBrush : panel;
    public static Brush Text => SystemParameters.HighContrast ? SystemColors.WindowTextBrush : text;
    public static Brush Muted => SystemParameters.HighContrast ? SystemColors.WindowTextBrush : muted;
    public static Brush Cyan => SystemParameters.HighContrast ? SystemColors.HighlightBrush : cyan;
    public static Brush Violet => SystemParameters.HighContrast ? SystemColors.WindowTextBrush : violet;
    public static Brush Amber => SystemParameters.HighContrast ? SystemColors.WindowTextBrush : amber;
    public static Brush Red => SystemParameters.HighContrast ? SystemColors.WindowTextBrush : red;
    public static Brush Line => SystemParameters.HighContrast ? SystemColors.WindowTextBrush : line;
    public static SolidColorBrush Brush(string hex) { var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)); b.Freeze(); return b; }
}

public abstract class Plot : FrameworkElement
{
    protected void Label(DrawingContext d, string text, double x, double y, Brush? color = null, double size = 11)
        => d.DrawText(new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), size, color ?? Palette.Muted, VisualTreeHelper.GetDpi(this).PixelsPerDip), new Point(x, y));
    protected Rect Area => new(54, 12, Math.Max(1, ActualWidth - 72), Math.Max(1, ActualHeight - 46));
    protected override void OnRender(DrawingContext d) { d.DrawRectangle(Brushes.Transparent, null, new Rect(RenderSize)); }
}
public sealed class HistoryPlot : Plot
{
    public IReadOnlyList<PointSample> Samples { get; set; } = Array.Empty<PointSample>();
    public DateTimeOffset From { get; set; }
    public DateTimeOffset To { get; set; }
    public bool ShowPcie { get; set; }
    public double? Reference { get; set; }
    public double GapSeconds { get; set; } = 5;
    public HistoryPlot()
    {
        Focusable = true;
        System.Windows.Automation.AutomationProperties.SetName(this, "Voltage history. Exact values are available in exported CSV and sensor details.");
        MouseMove += (_, e) =>
        {
            if (Samples.Count == 0) { ToolTip = "No voltage samples in this range"; return; }
            double f = Math.Clamp((e.GetPosition(this).X - Area.Left) / Area.Width, 0, 1);
            var t = From.AddSeconds((To - From).TotalSeconds * f);
            var p = Samples.MinBy(p => Math.Abs((p.Time - t).TotalSeconds))!;
            ToolTip = $"{p.Time.LocalDateTime:HH:mm:ss}  •  16-pin {p.Voltage:F3} V\nPCIe {p.Pcie:F3} V  •  comparison power {p.Power:F1} W\n{MainWindow.Friendly(p.Status)}";
        };
    }
    protected override void OnRender(DrawingContext d)
    {
        base.OnRender(d); var a = Area;
        if (Samples.Count == 0) { Label(d, "No samples in this range. Monitoring data will appear here.", a.Left, a.Top + 40); return; }
        var values = Samples.Select(p => p.Voltage).Concat(ShowPcie ? Samples.Where(p => p.Pcie.HasValue).Select(p => p.Pcie!.Value) : Array.Empty<double>()).ToList();
        if (Reference.HasValue) values.Add(Reference.Value);
        double lo = Math.Floor((values.Min() - .015) * 100) / 100, hi = Math.Ceiling((values.Max() + .015) * 100) / 100;
        hi = Math.Max(hi, lo + .05);
        double X(DateTimeOffset t) => a.Left + Math.Clamp((t - From).TotalSeconds / Math.Max(1, (To - From).TotalSeconds), 0, 1) * a.Width;
        double Y(double v) => a.Bottom - (v - lo) / (hi - lo) * a.Height;
        for (int i = 0; i <= 4; i++)
        {
            double v = lo + (hi - lo) * i / 4;
            d.DrawLine(new Pen(Palette.Line, 1), new Point(a.Left, Y(v)), new Point(a.Right, Y(v)));
            Label(d, v.ToString("F2"), 8, Y(v) - 7);
        }
        Label(d, "V", 8, 0);
        Label(d, From.LocalDateTime.ToString("HH:mm"), a.Left, a.Bottom + 9);
        Label(d, From.AddSeconds((To - From).TotalSeconds / 2).LocalDateTime.ToString("HH:mm"), a.Left + a.Width / 2 - 16, a.Bottom + 9);
        Label(d, To.LocalDateTime.ToString("HH:mm"), a.Right - 30, a.Bottom + 9);
        if (Reference.HasValue)
        {
            var pen = new Pen(Palette.Amber, 1) { DashStyle = DashStyles.Dash };
            d.DrawLine(pen, new(a.Left, Y(Reference.Value)), new(a.Right, Y(Reference.Value)));
            Label(d, "Reference", a.Right - 70, Math.Max(a.Top, Y(Reference.Value) - 17), Palette.Amber, 10);
        }
        // Split the raw sequence before aggregation so downsampling cannot bridge a real gap.
        var segments = new List<List<PointSample>>();
        foreach (var p in Samples)
        {
            if (segments.Count == 0 || (p.Time - segments[^1][^1].Time).TotalSeconds > GapSeconds) segments.Add(new());
            segments[^1].Add(p);
        }
        void Trace(bool pcie, Brush brush)
        {
            var pen = new Pen(brush, pcie ? 1.25 : 1.8); pen.Freeze();
            foreach (var segment in segments)
            {
                var draw = PlotData.Downsample(segment, (int)a.Width, From, To);
                Point? prior = null;
                foreach (var p in draw)
                {
                    double? value = pcie ? p.Pcie : p.Voltage;
                    if (!value.HasValue) { prior = null; continue; }
                    var pt = new Point(X(p.Time), Y(value.Value));
                    if (prior.HasValue) d.DrawLine(pen, prior.Value, pt);
                    else d.DrawEllipse(brush, null, pt, 1.5, 1.5);
                    prior = pt;
                }
            }
        }
        if (ShowPcie) Trace(true, Palette.Violet);
        Trace(false, Palette.Cyan);
        // One marker per incident transition, not a dot for every second of an alert.
        string previous = "";
        foreach (var p in Samples)
        { if (p.Alert && p.Status != previous) d.DrawEllipse(Palette.Red, new Pen(Palette.Panel, 1.5), new Point(X(p.Time), Y(p.Voltage)), 4, 4); previous = p.Status; }
    }
}
public sealed class HistogramPlot : Plot
{
    public HistogramResult Histogram { get; set; } = new(12, .01, Array.Empty<int>(), 0, null, null);
    public Baseline? Reference { get; set; }
    public string EmptyText { get; set; } = "Waiting for eligible samples in this load bin.";
    public HistogramPlot() { Focusable = true; System.Windows.Automation.AutomationProperties.SetName(this, "Voltage distribution, voltage in volts by sample count"); }
    protected override void OnRender(DrawingContext d)
    {
        base.OnRender(d); var a = Area; var h = Histogram;
        if (h.Total == 0) { Label(d, EmptyText, 8, 35); return; }
        double min = h.Start, max = h.Start + h.Counts.Length * h.Width;
        if (Reference?.Median is double rm) { min = Math.Min(min, rm - .005); max = Math.Max(max, rm + .005); }
        if (Reference?.P05 is double rp) { min = Math.Min(min, rp - .005); max = Math.Max(max, rp + .005); }
        double X(double v) => a.Left + (v - min) / (max - min) * a.Width;
        int peak = Math.Max(1, h.Counts.Max());
        for (int i = 0; i <= 2; i++)
        {
            double y = a.Bottom - a.Height * i / 2;
            d.DrawLine(new Pen(Palette.Line, 1), new(a.Left, y), new(a.Right, y));
            Label(d, ((int)Math.Ceiling(peak * i / 2.0)).ToString(), 12, y - 8);
        }
        for (int i = 0; i < h.Counts.Length; i++)
        {
            double x = X(h.Start + i * h.Width), width = X(h.Start + (i + 1) * h.Width) - x;
            double height = h.Counts[i] / (double)peak * a.Height;
            d.DrawRoundedRectangle(Palette.Cyan, null, new Rect(x + 1, a.Bottom - height, Math.Max(.5, width - 2), height), 2, 2);
        }
        void Marker(double? value, Brush color) { if (value.HasValue) d.DrawLine(new Pen(color, 1.5) { DashStyle = DashStyles.Dash }, new(X(value.Value), a.Top), new(X(value.Value), a.Bottom)); }
        Marker(Reference?.Median, Palette.Amber); Marker(Reference?.P05, Palette.Violet);
        Label(d, min.ToString("F3") + " V", a.Left, a.Bottom + 9);
        Label(d, max.ToString("F3") + " V", a.Right - 54, a.Bottom + 9);
        ToolTip = $"{h.Total:N0} unique samples • bucket {h.Width:F2} V\nMedian {h.Median:F3} V • P05 {h.P05:F3} V\nAmber: reference median. Violet: reference P05.";
    }
}
