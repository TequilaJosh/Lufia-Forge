using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Color = System.Windows.Media.Color;
using Point = System.Windows.Point;
using Size = System.Windows.Size;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Pen = System.Windows.Media.Pen;
using Cursors = System.Windows.Input.Cursors;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;

namespace LufiaForge.Modules.Common;

/// <summary>One line of a <see cref="GrowthChart"/>: a value per level (index 0 = level 1).</summary>
public sealed record ChartSeries(string Name, double[] Values, Color Color, bool Highlight = false);

/// <summary>
/// A small line chart of stats (or EXP) by level: one line per series, the highlighted one drawn thick, the others thin.
/// Levels along the bottom, the value up the side. With <see cref="HandleLevels"/> set, the highlighted line gets a handle
/// at each of those levels (the end of a band of levels): dragging one up or down shows the new shape (the bands on both
/// sides of the handle bend, the rest stays) and, when let go, raises <see cref="HandleDropped"/> with the handle and the
/// value wanted there, for the page to turn into gains per level.
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

    public static readonly DependencyProperty HandleLevelsProperty = DependencyProperty.Register(nameof(HandleLevels), typeof(IReadOnlyList<int>),
        typeof(GrowthChart), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>The levels (1-based) the highlighted line can be dragged at; null: not editable.</summary>
    public IReadOnlyList<int>? HandleLevels
    {
        get => (IReadOnlyList<int>?)GetValue(HandleLevelsProperty);
        set => SetValue(HandleLevelsProperty, value);
    }

    /// <summary>A handle was let go: its index in <see cref="HandleLevels"/> and the value wanted at its level.</summary>
    public event Action<int, double>? HandleDropped;

    private Point? _mouse;
    private int _drag = -1, _hover = -1;
    private double _dragValue, _dragMax;

    private const double Left = 54, Right = 12, Top = 10, Bottom = 22;

    public GrowthChart()
    {
        MinHeight = 160; MinWidth = 280;
        MouseMove += OnMove;
        MouseLeave += (_, _) => { _mouse = null; if (_drag < 0) _hover = -1; InvalidateVisual(); };
        MouseLeftButtonDown += (_, e) =>
        {
            if (_hover < 0 || Highlighted is not { } s || HandleLevels is not { } lv) return;
            _dragMax = Scale(s);
            _drag = _hover;
            _dragValue = s.Values[Math.Clamp(lv[_drag] - 1, 0, s.Values.Length - 1)];
            CaptureMouse();
            e.Handled = true;
        };
        MouseLeftButtonUp += (_, e) =>
        {
            if (_drag < 0) return;
            int k = _drag; double v = _dragValue;
            _drag = -1;
            ReleaseMouseCapture();
            InvalidateVisual();
            HandleDropped?.Invoke(k, v);
            e.Handled = true;
        };
    }

    private ChartSeries? Highlighted => Series?.FirstOrDefault(s => s.Highlight);

    private void OnMove(object sender, MouseEventArgs e)
    {
        _mouse = e.GetPosition(this);
        double h = ActualHeight;
        if (_drag >= 0)
        {
            double y = Math.Clamp(_mouse.Value.Y, Top, h - Bottom);
            _dragValue = Math.Max(0, Math.Round((1 - (y - Top) / (h - Top - Bottom)) * _dragMax));
        }
        else
        {
            _hover = -1;
            if (Highlighted is { } s && HandleLevels is { } lv)
            {
                double max = Scale(s);
                for (int k = 0; k < lv.Count; k++)
                {
                    int i = Math.Clamp(lv[k] - 1, 0, s.Values.Length - 1);
                    var p = new Point(X(i, s.Values.Length), Y(s.Values[i], max));
                    if ((p - _mouse.Value).Length <= 8) { _hover = k; break; }
                }
            }
            Cursor = _hover >= 0 ? Cursors.SizeNS : null;
        }
        InvalidateVisual();
    }

    protected override Size MeasureOverride(Size available) => new(double.IsInfinity(available.Width) ? 520 : Math.Max(MinWidth, available.Width), 220);

    private static readonly Typeface Face = new("Segoe UI");

    private double X(int i, int levels) => Left + (ActualWidth - Left - Right) * i / Math.Max(1, levels - 1);
    private double Y(double v, double max) => Top + (ActualHeight - Top - Bottom) * (1 - Math.Clamp(v / max, 0, 1.05));

    /// <summary>The top of the scale for the line(s) shown (kept while a handle is dragged).</summary>
    private double Scale(ChartSeries s)
    {
        if (_drag >= 0) return _dragMax;
        var series = Series!;
        var shown = series.Where(x => x.Highlight).DefaultIfEmpty(series[0]).ToList();
        double max = Math.Max(1, shown.Max(x => x.Values.Length == 0 ? 0 : x.Values.Max()));
        double step = Nice(max / 4);
        return Math.Ceiling(max * 1.15 / step) * step;   // room above the line to drag it up
    }

    /// <summary>The highlighted line while a handle is dragged: the two bands next to it bend, the rest stays.</summary>
    private double[] Preview(ChartSeries s)
    {
        var v = (double[])s.Values.Clone();
        var lv = HandleLevels!;
        int at = lv[_drag] - 1, from = _drag > 0 ? lv[_drag - 1] - 1 : 0, to = _drag + 1 < lv.Count ? lv[_drag + 1] - 1 : -1;
        double delta = _dragValue - s.Values[at];
        for (int i = from + 1; i <= at && i < v.Length; i++) v[i] += delta * (i - from) / Math.Max(1, at - from);
        if (to > at) for (int i = at + 1; i < to && i < v.Length; i++) v[i] += delta * (to - i) / (to - at);
        else for (int i = at + 1; i < v.Length; i++) v[i] += delta;   // the last handle moves what comes after it
        return v;
    }

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
        double max = Scale(shown[0]);
        double step = Nice(max / 5);
        for (double v = 0; v <= max + step / 2; v += step)
        {
            dc.DrawLine(grid, new Point(Left, Y(v, max)), new Point(w - Right, Y(v, max)));
            var t = Text(v >= 10000 ? $"{v / 1000:0}k" : $"{v:0}", 10, label);
            dc.DrawText(t, new Point(Left - 6 - t.Width, Y(v, max) - t.Height / 2));
        }
        foreach (int lv in new[] { 1, 10, 20, 30, 40, 50, 60, 70, 80, 90, 99 })
        {
            if (lv > levels) continue;
            dc.DrawLine(grid, new Point(X(lv - 1, levels), Top), new Point(X(lv - 1, levels), h - Bottom));
            var t = Text(lv.ToString(), 10, label);
            dc.DrawText(t, new Point(X(lv - 1, levels) - t.Width / 2, h - Bottom + 4));
        }
        // the others first (thin and faint), the highlighted on top
        foreach (var s in series.OrderBy(s => s.Highlight))
        {
            var values = s.Highlight && _drag >= 0 && HandleLevels != null ? Preview(s) : s.Values;
            if (values.Length < 2) continue;
            var c = s.Color;
            var pen = new Pen(new SolidColorBrush(s.Highlight ? c : Color.FromArgb(0x70, c.R, c.G, c.B)), s.Highlight ? 2.5 : 1.2);
            var g = new StreamGeometry();
            using (var ctx = g.Open())
            {
                ctx.BeginFigure(new Point(X(0, levels), Y(values[0], max)), false, false);
                for (int i = 1; i < values.Length; i++) ctx.LineTo(new Point(X(i, levels), Y(values[i], max)), true, false);
            }
            g.Freeze();
            dc.DrawGeometry(null, pen, g);
            // the handles
            if (s.Highlight && HandleLevels is { } hl)
                for (int k = 0; k < hl.Count; k++)
                {
                    int i = Math.Clamp(hl[k] - 1, 0, values.Length - 1);
                    bool hot = k == _hover || k == _drag;
                    dc.DrawEllipse(hot ? Brushes.White : new SolidColorBrush(c), new Pen(Brushes.White, 1), new Point(X(i, levels), Y(values[i], max)), hot ? 6 : 4, hot ? 6 : 4);
                }
        }
        // the value under the mouse (or the one being dragged)
        if (_mouse is Point m && m.X >= Left && m.X <= w - Right)
        {
            int i = _drag >= 0 ? HandleLevels![_drag] - 1 : Math.Clamp((int)Math.Round((m.X - Left) / (w - Left - Right) * (levels - 1)), 0, levels - 1);
            dc.DrawLine(new Pen(label, 1), new Point(X(i, levels), Top), new Point(X(i, levels), h - Bottom));
            string text = _drag >= 0
                ? $"Level {i + 1}: {shown[0].Name} {_dragValue:#,0} (let go to set it)"
                : $"Level {i + 1}: " + string.Join(", ", shown.Where(s => i < s.Values.Length).Select(s => $"{s.Name} {s.Values[i]:#,0}"))
                  + (_hover >= 0 ? "  (drag the dot up or down)" : "");
            var t = Text(text, 11, Brushes.White);
            double tx = Math.Max(Left, Math.Min(X(i, levels) + 6, w - Right - t.Width - 4));
            dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(0xC0, 0x20, 0x10, 0x30)), null, new Rect(tx - 3, Top, t.Width + 6, t.Height + 2));
            dc.DrawText(t, new Point(tx, Top + 1));
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
