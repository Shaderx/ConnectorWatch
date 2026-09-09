using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Media;

namespace ConnectorWatch.Gui;

public sealed record TrendPlotPoint(DateTimeOffset Time, double? Median, double? Low, double? High,
    int Segment, int Count, string Evidence);

public sealed class ElectricalTrendPlot : Plot
{
    public IReadOnlyList<TrendPlotPoint> Points { get; set; } = Array.Empty<TrendPlotPoint>();
    public DateTimeOffset From { get; set; }
    public DateTimeOffset To { get; set; }
    public string EmptyText { get; set; } = "No comparable electrical observations in this range.";
    public ElectricalTrendPlot()
    {
        Focusable = true;
        System.Windows.Automation.AutomationProperties.SetName(this,
            "Electrical degradation trend. Millivolts below comparison voltage at one load band; higher means more voltage drop. Shading is observed spread.");
        MouseMove += (_, e) =>
        {
            if (Points.Count == 0) { ToolTip = EmptyText; return; }
            double fraction = Math.Clamp((e.GetPosition(this).X - Area.Left) / Area.Width, 0, 1);
            var time = From.AddSeconds((To - From).TotalSeconds * fraction);
            var point = Points.MinBy(p => Math.Abs((p.Time - time).TotalSeconds))!;
            ToolTip = $"{point.Time.LocalDateTime:yyyy-MM-dd HH:mm:ss}\n" +
                (point.Median.HasValue ? $"Median drop {point.Median:+0.0;-0.0;0.0} mV\nObserved P05–P95 {point.Low:F1} to {point.High:F1} mV\n" : "Insufficient observations for an aggregate\n") + point.Evidence;
        };
    }
    protected override void OnRender(DrawingContext d)
    {
        base.OnRender(d);
        var a = Area;
        var supported = Points.Where(p => p.Median.HasValue && p.Low.HasValue && p.High.HasValue).ToArray();
        if (supported.Length == 0) { Label(d, EmptyText, a.Left, a.Top + 36); return; }
        double lo = Math.Floor((Math.Min(0, supported.Min(p => p.Low!.Value)) - 5) / 10) * 10;
        double hi = Math.Ceiling((Math.Max(0, supported.Max(p => p.High!.Value)) + 5) / 10) * 10;
        hi = Math.Max(hi, lo + 20);
        double X(DateTimeOffset time) => a.Left + Math.Clamp((time - From).TotalSeconds / Math.Max(1, (To - From).TotalSeconds), 0, 1) * a.Width;
        double Y(double value) => a.Bottom - (value - lo) / (hi - lo) * a.Height;
        for (int i = 0; i <= 4; i++)
        {
            double v = lo + (hi - lo) * i / 4;
            d.DrawLine(new Pen(Palette.Line, 1), new(a.Left, Y(v)), new(a.Right, Y(v)));
            Label(d, v.ToString("F0"), 8, Y(v) - 7);
        }
        Label(d, "mV", a.Right - 20, 0);
        d.DrawLine(new Pen(Palette.Muted, 1) { DashStyle = DashStyles.Dash }, new(a.Left, Y(0)), new(a.Right, Y(0)));
        Label(d, From.LocalDateTime.ToString("HH:mm"), a.Left, a.Bottom + 9);
        Label(d, To.LocalDateTime.ToString("HH:mm"), a.Right - 30, a.Bottom + 9);
        TrendPlotPoint? previous = null;
        foreach (var point in Points)
        {
            if (!point.Median.HasValue || !point.Low.HasValue || !point.High.HasValue)
            {
                d.DrawEllipse(null, new Pen(Palette.Muted, 1), new(X(point.Time), a.Bottom - 3), 2, 2);
                previous = null; continue;
            }
            var pt = new Point(X(point.Time), Y(point.Median.Value));
            var brush = Palette.Cyan;
            if (previous?.Median.HasValue == true && previous.Segment == point.Segment)
            {
                var polygon = new StreamGeometry();
                using (var c = polygon.Open())
                {
                    c.BeginFigure(new(X(previous.Time), Y(previous.Low!.Value)), true, true);
                    c.LineTo(new(X(previous.Time), Y(previous.High!.Value)), true, false);
                    c.LineTo(new(X(point.Time), Y(point.High.Value)), true, false);
                    c.LineTo(new(X(point.Time), Y(point.Low.Value)), true, false);
                }
                polygon.Freeze();
                d.PushOpacity(.18); d.DrawGeometry(brush, null, polygon); d.Pop();
                d.DrawLine(new Pen(brush, 1.8), new(X(previous.Time), Y(previous.Median.Value)), pt);
            }
            else
            {
                d.DrawLine(new Pen(brush, 1), new(pt.X, Y(point.Low.Value)), new(pt.X, Y(point.High.Value)));
                d.DrawEllipse(brush, null, pt, 2, 2);
            }
            previous = point;
        }
    }
}
