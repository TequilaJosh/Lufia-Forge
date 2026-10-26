using System.Globalization;
using System.Windows;
using System.Windows.Media;
using Color = System.Windows.Media.Color;
using Point = System.Windows.Point;
using Size = System.Windows.Size;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Pen = System.Windows.Media.Pen;

namespace LufiaForge.Modules.Common;

/// <summary>One line of a <see cref="GrowthChart"/>: a value per level (index 0 = level 1).</summary>
public sealed record ChartSeries(string Name, double[] Values, Color Color, bool Highlight = false);

/// <summary>
/// A small line chart of stats (or EXP) by level: one line per series, the highlighted one drawn thick with its value
/// at a few levels, the others thin. Levels along the bottom, the value up the side.
/// </summary>
public sealed class GrowthChart : FrameworkElement
{
    public static readonly DependencyProperty SeriesProperty = DependencyProperty.Register(nameof(Series), typeof(IReadOnlyList<ChartSeries>),
        typeof(GrowthChart), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public IReadOnlyList<ChartSeries>? Series
    {
        get => (IReadOnlyList<ChartSeries>?)GetValue(SeriesProperty);
        set => SetValue(SeriesProperty, value);
    }

    /// <summary>Show the values (a number per level) as "level N: value" when the mouse is over the chart.</summary>
    private Point? _mouse;

    public GrowthChart()
    {
        MinHeight = 160; MinWidth = 280;
        MouseMove += (_, e) => { _mouse = e.GetPosition(this); InvalidateVisual(); };
        MouseLeave += (_, _) => { _mouse = null; InvalidateVisual(); };
    }

    protected override Size MeasureOverride(Size available) => new(double.IsInfinity(available.Width) ? 520 : Math.Max(MinWidth, available.Width), 220);

    private static readonly Typeface Face = new("Segoe UI");

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(0x30, 0, 0, 0)), null, new Rect(0, 0, w, h));
        var series = Series;
        if (series == null || series.Count == 0 || w < 60 || h < 40) return;
        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var label = new SolidColorBrush(Color.FromRgb(0xB8, 0xA8, 0xD8));
        var grid = new Pen(new SolidColorBrush(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF)), 1);
        FormattedText Text(string s, double size, Brush b) => new(s, CultureInfo.CurrentCulture, System.Windows.FlowDirection.LeftToRight, Face, size, b, dpi);

        int levels = series.Max(s => s.Values.Length);
        var shown = series.Where(s => s.Highlight).DefaultIfEmpty(series[0]).ToList();
        double max = Math.Max(1, shown.Max(s => s.Values.Length == 0 ? 0 : s.Values.Max()));
        double step = Nice(max / 4);
        max = Math.Ceiling(max / step) * step;
        double left = 54, right = 12, top = 10, bottom = 22;
        double X(int i) => left + (w - left - right) * i / Math.Max(1, levels - 1);
        double Y(double v) => top + (h - top - bottom) * (1 - Math.Clamp(v / max, 0, 1.05));

        for (double v = 0; v <= max + step / 2; v += step)
        {
            dc.DrawLine(grid, new Point(left, Y(v)), new Point(w - right, Y(v)));
            var t = Text(v >= 10000 ? $"{v / 1000:0}k" : $"{v:0}", 10, label);
            dc.DrawText(t, new Point(left - 6 - t.Width, Y(v) - t.Height / 2));
        }
        foreach (int lv in new[] { 1, 10, 20, 30, 40, 50, 60, 70, 80, 90, 99 })
        {
            if (lv > levels) continue;
            dc.DrawLine(grid, new Point(X(lv - 1), top), new Point(X(lv - 1), h - bottom));
            var t = Text(lv.ToString(), 10, label);
            dc.DrawText(t, new Point(X(lv - 1) - t.Width / 2, h - bottom + 4));
        }
        // the others first (thin and faint), the highlighted on top
        foreach (var s in series.OrderBy(s => s.Highlight))
        {
            if (s.Values.Length < 2) continue;
            var c = s.Color;
            var pen = new Pen(new SolidColorBrush(s.Highlight ? c : Color.FromArgb(0x70, c.R, c.G, c.B)), s.Highlight ? 2.5 : 1.2);
            var g = new StreamGeometry();
            using (var ctx = g.Open())
            {
                ctx.BeginFigure(new Point(X(0), Y(s.Values[0])), false, false);
                for (int i = 1; i < s.Values.Length; i++) ctx.LineTo(new Point(X(i), Y(s.Values[i])), true, false);
            }
            g.Freeze();
            dc.DrawGeometry(null, pen, g);
        }
        // the value under the mouse
        if (_mouse is Point m && m.X >= left && m.X <= w - right)
        {
            int i = (int)Math.Round((m.X - left) / (w - left - right) * (levels - 1));
            i = Math.Clamp(i, 0, levels - 1);
            dc.DrawLine(new Pen(label, 1), new Point(X(i), top), new Point(X(i), h - bottom));
            var lines = shown.Where(s => i < s.Values.Length).Select(s => $"{s.Name} {s.Values[i]:#,0}").ToList();
            var t = Text($"Level {i + 1}: " + string.Join(", ", lines), 11, Brushes.White);
            double tx = Math.Min(X(i) + 6, w - right - t.Width - 4);
            dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(0xC0, 0x20, 0x10, 0x30)), null, new Rect(tx - 3, top, t.Width + 6, t.Height + 2));
            dc.DrawText(t, new Point(tx, top + 1));
        }
    }

    private static double Nice(double x)
    {
        if (x <= 0) return 1;
        double p = Math.Pow(10, Math.Floor(Math.Log10(x)));
        double f = x / p;
        return (f <= 1 ? 1 : f <= 2 ? 2 : f <= 5 ? 5 : 10) * p;
    }
}
